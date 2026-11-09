"""Optional real-pi smoke checks. Python 3 stdlib + installed pi + published slim.

No model prompts are submitted: the script checks command registration first,
invokes extension commands only, and requires an empty/non-streaming session.
The isolated offline pi agent directory receives no credentials or packages.
Private review URLs stay in memory and are never printed or persisted.
"""
import argparse
import json
import os
from pathlib import Path
import queue
import shutil
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.parse
import urllib.request

ROOT = Path(__file__).resolve().parents[1]


def require(value, message):
    if not value:
        raise AssertionError(message)


class Pi:
    def __init__(self, executable, repository, agent, dll):
        Path(agent, 'settings.json').write_text(json.dumps({
            'enableInstallTelemetry': False, 'enableAnalytics': False,
            'cacheWarming': 'off', 'compaction': {'enabled': False},
        }), encoding='utf-8')
        env = dict(os.environ, PI_CODING_AGENT_DIR=str(agent), PI_OFFLINE='1',
                   DIFF_REVIEW_SLIM_DLL=str(dll), DIFF_REVIEW_SLIM_NO_OPEN='1')
        self.process = subprocess.Popen([
            executable, '--mode', 'rpc', '--no-session', '--offline',
            '--no-extensions', '--no-skills', '--no-prompt-templates',
            '--no-themes', '--no-context-files', '--no-approve', '--no-tools',
            '-e', str(ROOT / 'integrations/pi.ts'),
        ], cwd=repository, env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.PIPE)
        self.queue = queue.Queue()
        self.records = []
        self.errors = []
        self.sequence = 0

        def read_stdout():
            for line in self.process.stdout:  # binary LF framing, not Unicode splitlines
                try:
                    self.queue.put(json.loads(line))
                except ValueError:
                    self.queue.put({'type': 'invalid_protocol'})
            self.queue.put({'type': 'process_ended'})

        def read_stderr():
            for line in self.process.stderr:
                # Only keep bounded diagnostics; do not print provider/catalog data.
                if len(self.errors) < 50:
                    self.errors.append(line.decode('utf-8', 'replace')[:500])

        threading.Thread(target=read_stdout, daemon=True).start()
        threading.Thread(target=read_stderr, daemon=True).start()

    def send(self, kind, **fields):
        self.sequence += 1
        identifier = 'smoke-' + str(self.sequence)
        self.process.stdin.write((json.dumps(dict(id=identifier, type=kind, **fields)) + '\n').encode())
        self.process.stdin.flush()
        return identifier

    def wait(self, predicate, timeout=30):
        deadline = time.monotonic() + timeout
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise AssertionError('Timed out waiting for pi extension')
            try:
                record = self.queue.get(timeout=remaining)
            except queue.Empty:
                raise AssertionError('Timed out waiting for pi extension') from None
            require(record.get('type') != 'invalid_protocol', 'Non-JSON output corrupted pi RPC')
            require(record.get('type') != 'process_ended', 'pi exited unexpectedly')
            require(record.get('type') not in ('agent_start', 'turn_start', 'message_start'),
                    'Extension unexpectedly started agent/model work')
            self.records.append(record)
            if predicate(record):
                return record

    def response(self, identifier):
        # A concurrent response may already have arrived while awaiting another.
        record = next((r for r in self.records if r.get('type') == 'response' and r.get('id') == identifier), None)
        record = record or self.wait(lambda r: r.get('type') == 'response' and r.get('id') == identifier)
        require(record.get('success'), 'pi rejected an RPC command')
        return record.get('data', {})

    def begin(self, base=''):
        start = len(self.records)
        identifier = self.send('prompt', message='/diff-review-slim' + (' ' + base if base else ''))
        notice = self.wait(lambda r: r.get('type') == 'extension_ui_request' and
                           r.get('method') == 'notify' and r.get('message', '').startswith('Slim review URL (private): '))
        url = notice['message'][len('Slim review URL (private): '):]
        require(urllib.parse.urlsplit(url).fragment, 'Review token was not provided')
        return identifier, start, url

    def handled(self, identifier, start, expect_draft=False):
        require(self.response(identifier).get('disposition') == 'handled', 'Slash command was not consumed')
        drafts = [r for r in self.records[start:] if r.get('type') == 'extension_ui_request' and
                  r.get('method') == 'set_editor_text']
        require(len(drafts) == (1 if expect_draft else 0), 'Unexpected editor change or duplicate draft')
        return drafts[0]['text'] if drafts else None

    def no_model_work(self):
        messages = self.response(self.send('get_messages')).get('messages')
        require(messages == [], 'Review leaked into conversation/model context')
        state = self.response(self.send('get_state'))
        require(not state.get('isStreaming') and state.get('messageCount') == 0 and state.get('pendingMessageCount') == 0,
                'Review started or queued agent work')

    def close(self):
        if self.process.poll() is not None:
            return
        self.process.stdin.close()
        try:
            self.process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            if os.name == 'nt':
                subprocess.run(['taskkill', '/PID', str(self.process.pid), '/T', '/F'], capture_output=True)
            else:
                self.process.kill()
            self.process.wait(timeout=10)
            raise AssertionError('pi did not shut down cleanly') from None


def api(url, path, body=None):
    parsed = urllib.parse.urlsplit(url)
    request = urllib.request.Request(urllib.parse.urlunsplit((parsed.scheme, parsed.netloc, path, '', '')),
        data=None if body is None else json.dumps(body).encode(),
        headers={'X-Review-Token': parsed.fragment, 'Content-Type': 'application/json'})
    return urllib.request.urlopen(request, timeout=10)


def run(executable, dll):
    with tempfile.TemporaryDirectory(prefix='slim-pi-smoke-') as temporary:
        root = Path(temporary)
        repository = root / 'repo'
        agent = root / 'agent'
        repository.mkdir()
        agent.mkdir()
        subprocess.run(['git', 'init', '-q', '-b', 'main'], cwd=repository, check=True, capture_output=True)
        (repository / 'file.txt').write_text('new line\n', encoding='utf-8')
        pi = Pi(executable, repository, agent, dll)
        try:
            commands = pi.response(pi.send('get_commands')).get('commands', [])
            names = {command['name'] for command in commands if command['source'] == 'extension'}
            require({'diff-review-slim', 'diff-review-slim-cancel'} <= names, 'Slim commands did not load')
            print('PASS real pi loads the optional extension')

            identifier, start, url = pi.begin()
            with api(url, '/api/session') as response:
                metadata = json.load(response)
            require(Path(metadata['repository']).resolve() == repository.resolve(), 'Adapter reviewed the wrong checkout')
            file_id = metadata['files'][0]['id']
            with api(url, '/api/files/' + file_id) as response:
                require(json.load(response)['rows'][0]['newLine'] == 1, 'Diff did not load')
            with api(url, '/api/submit', {'overallNote': 'Pi draft overall feedback', 'comments': [
                {'id': 'c1', 'fileId': file_id, 'side': 'new', 'startLine': 1, 'endLine': 1, 'body': 'Pi inline feedback'},
            ]}) as response:
                require(json.load(response)['status'] == 'submitted', 'Review did not submit')
            draft = pi.handled(identifier, start, expect_draft=True)
            require('Pi draft overall feedback' in draft and 'file.txt:1 (new)' in draft and 'Pi inline feedback' in draft,
                    'Wrong or incomplete feedback draft')
            pi.no_model_work()
            print('PASS submit inserts exactly one draft; no auto-send, messages or model turn')

            for action in ('cancel', 'empty', 'close', 'pi-cancel'):
                identifier, start, url = pi.begin()
                if action == 'cancel':
                    duplicate = pi.send('prompt', message='/diff-review-slim')
                    require(pi.response(duplicate).get('disposition') == 'handled', 'Duplicate command was not consumed')
                    require(any(r.get('message') == 'A slim review is already open.' for r in pi.records[start:]),
                            'Concurrent-review guard did not run')
                    with api(url, '/api/cancel', {}):
                        pass
                elif action == 'empty':
                    with api(url, '/api/submit', {'overallNote': ' ', 'comments': []}):
                        pass
                elif action == 'close':
                    connection = api(url, '/api/lifetime')
                    require(connection.readline() == b'connected\n', 'No lifetime acknowledgement')
                    connection.close()
                else:
                    cancel_id = pi.send('prompt', message='/diff-review-slim-cancel')
                    pi.response(cancel_id)
                pi.handled(identifier, start)
                pi.no_model_work()
                print('PASS ' + action + ' leaves editor untouched and starts no model work')

            start = len(pi.records)
            identifier = pi.send('prompt', message='/diff-review-slim nonexistent-base-for-smoke-check')
            pi.handled(identifier, start)
            require(any(r.get('method') == 'notify' and r.get('notifyType') == 'error' for r in pi.records[start:]),
                    'CLI failure was not reported')
            pi.no_model_work()
            print('PASS CLI errors do not insert or send feedback')

            _, _, url = pi.begin()
            pi.close()
            try:
                with api(url, '/api/session'):
                    pass
            except urllib.error.HTTPError:
                raise AssertionError('Reviewer still answered HTTP after pi shutdown') from None
            except (urllib.error.URLError, ConnectionError, TimeoutError):
                pass
            else:
                raise AssertionError('Reviewer was left running after pi shutdown')
            print('PASS pi shutdown cancels its owned reviewer')
        finally:
            pi.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pi', default=shutil.which('pi.cmd' if os.name == 'nt' else 'pi'))
    parser.add_argument('--dll', type=Path, default=ROOT / 'artifacts/publish/diff-review-slim.dll')
    args = parser.parse_args()
    require(args.pi, 'Install/use an existing pi executable, or provide --pi')
    require(args.dll.is_file(), 'Publish the .NET app first, or provide --dll')
    run(args.pi, args.dll.resolve())
