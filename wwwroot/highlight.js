'use strict';
// Lightweight, dependency-free syntax colouring for the review diff.
//
// This is not a parser and does not aim to be one. It carries block-comment and
// multi-line string state from line to line, colours strings, numbers, comments
// and keywords, and classifies identifiers by shape (keyword, type, call,
// property). Colours are CSS classes (see style.css), so no inline styles and
// no HTML is generated that was not escaped here first.
(() => {
  const escapeHtml = text => text.replace(/[&<>]/g, c => c === '&' ? '&amp;' : c === '<' ? '&lt;' : '&gt;');
  const words = text => new Set(text.split(/\s+/).filter(Boolean));

  // Shared C-family control keywords render magenta, like VS Code Dark Modern.
  const CONTROL = words(`break case catch continue default do else finally for foreach goto
    if in is lock return switch throw try using when where while yield await as`);

  const CS_SHARED = words(`abstract async base class const delegate enum event explicit extern
    fixed get implicit init interface internal namespace new operator out override params partial
    private protected public readonly record ref required sealed set sizeof stackalloc static struct
    this typeof unchecked unsafe var virtual void volatile with file global dynamic string int long
    short byte sbyte uint ulong ushort float double decimal bool char object byte nint nuint nameof`);

  const CS = {
    mode: 'code',
    block: ['/*', '*/'],
    line: '//',
    preproc: true,
    keywords: CS_SHARED,
    control: CONTROL,
    constants: words('true false null'),
    strings: [
      { open: '@$"', close: '"', double: true, multiline: true },
      { open: '$@"', close: '"', double: true, multiline: true },
      { open: '@"', close: '"', double: true, multiline: true },
      { open: '"""', close: '"""', multiline: true },
      { open: '$"', close: '"', escape: true },
      { open: '"', close: '"', escape: true },
      { open: "'", close: "'", escape: true },
    ],
  };

  const JS = {
    mode: 'code',
    block: ['/*', '*/'],
    line: '//',
    keywords: words(`abstract any async boolean class const declare enum export extends function
      implements import interface keyof let namespace never number object private protected public
      readonly satisfies static string symbol type undefined unique unknown var void`),
    control: CONTROL,
    constants: words('true false null undefined this super'),
    strings: [
      { open: '`', close: '`', escape: true, multiline: true },
      { open: '"', close: '"', escape: true },
      { open: "'", close: "'", escape: true },
    ],
    regex: true,
  };

  const PY = {
    mode: 'code',
    line: '#',
    keywords: words(`and as assert async await class def del elif except finally from global import
      in is lambda nonlocal not or pass raise return try while with yield match case`),
    control: words('break continue else for if return with'),
    constants: words('True False None self cls'),
    strings: [
      { open: '"""', close: '"""', escape: true, multiline: true },
      { open: "'''", close: "'''", escape: true, multiline: true },
      { open: '"', close: '"', escape: true },
      { open: "'", close: "'", escape: true },
    ],
  };

  const SHELL = {
    mode: 'code',
    line: '#',
    keywords: words(`alias case do done elif else esac export fi for function if in local readonly
      return select set source then time until unset while`),
    control: words('break continue exit return'),
    constants: words('true false'),
    strings: [
      { open: '"', close: '"', escape: true, multiline: true },
      { open: "'", close: "'", multiline: true },
    ],
  };

  const YAML = {
    mode: 'code',
    line: '#',
    keywords: words('true false yes no on off null'),
    control: new Set(),
    constants: words('true false null'),
    strings: [
      { open: '"', close: '"', escape: true },
      { open: "'", close: "'" },
    ],
    properties: true,
  };

  const SQL = {
    mode: 'code',
    line: '--',
    block: ['/*', '*/'],
    keywords: words(`select from where group by order having insert into values update set delete
      create table index alter drop join left right inner outer full cross on as distinct union all
      limit offset returning exists between like ilike beginning commit rollback transaction
      primary key foreign references default case when then else end and or not in is null`),
    control: new Set(),
    constants: words('null true false'),
    strings: [
      { open: '"', close: '"', escape: true },
      { open: "'", close: "'", double: true },
    ],
  };

  const MARKUP = { mode: 'markup' };
  const CSS = { mode: 'css' };
  const JSON_LANG = { mode: 'json' };
  const MARKDOWN = { mode: 'markdown' };

  const BY_EXTENSION = {
    cs: CS, csx: CS, fs: CS, vb: CS,
    js: JS, mjs: JS, cjs: JS, jsx: JS, ts: JS, tsx: JS, mts: JS, cts: JS, json: JSON_LANG,
    jsonc: JSON_LANG, webmanifest: JSON_LANG,
    css: CSS, scss: CSS, less: CSS,
    html: MARKUP, htm: MARKUP, xml: MARKUP, xaml: MARKUP, svg: MARKUP, csproj: MARKUP, props: MARKUP,
    targets: MARKUP, nuspec: MARKUP, config: MARKUP, razor: MARKUP, cshtml: MARKUP, vue: MARKUP,
    py: PY, pyi: PY,
    sh: SHELL, bash: SHELL, zsh: SHELL, ps1: SHELL, psd1: SHELL, psm1: SHELL, dockerfile: SHELL,
    yml: YAML, yaml: YAML, toml: YAML, ini: YAML, cfg: YAML, env: YAML,
    sql: SQL,
    md: MARKDOWN, markdown: MARKDOWN,
  };

  function languageFor(path) {
    if (!path) return null;
    const name = String(path).split(/[\\/]/).pop().toLowerCase();
    if (name === 'dockerfile' || name === 'makefile') return BY_EXTENSION[name] || SHELL;
    const dot = name.lastIndexOf('.');
    if (dot < 0) return null;
    return BY_EXTENSION[name.slice(dot + 1)] || null;
  }

  function createState(spec) {
    return { spec, block: false, string: null, fence: false, commentOnly: false };
  }

  function findStringEnd(string, text, from) {
    let i = from;
    while (i < text.length) {
      if (string.escape && text[i] === '\\') { i += 2; continue; }
      if (string.double && text.startsWith(string.close + string.close, i)) { i += string.close.length * 2; continue; }
      if (text.startsWith(string.close, i)) return i + string.close.length;
      i++;
    }
    return -1;
  }

  const IDENT = /[A-Za-z_$][\w$]*/y;
  const NUMBER = /(?:0[xXbBoO])?[\d_]+(?:\.\d+)?(?:[eE][+-]?\d+)?[a-zA-Z]{0,3}/y;
  const isUpperStart = word => /^[A-Z]/.test(word);

  // Generic C-like / hash-comment scanner.
  function scanCode(state, text) {
    const spec = state.spec;
    const out = [];
    let plain = '';
    let i = 0;
    const flush = () => { if (plain) { out.push(escapeHtml(plain)); plain = ''; } };
    const span = (cls, value) => { flush(); out.push(`<span class="${cls}">${escapeHtml(value)}</span>`); };

    if (spec.preproc && /^\s*#/.test(text)) {
      const lead = text.match(/^\s*/)[0].length;
      plain += text.slice(0, lead);
      const directive = /^#\s*\w+/.exec(text.slice(lead));
      if (directive) { span('tk-pre', directive[0]); i = lead + directive[0].length; }
    }

    while (i < text.length) {
      if (state.block) {
        const close = text.indexOf(spec.block[1], i);
        if (close < 0) { span('tk-com', text.slice(i)); return out.join(''); }
        const end = close + spec.block[1].length;
        span('tk-com', text.slice(i, end)); i = end; state.block = false; continue;
      }
      if (state.string) {
        const end = findStringEnd(state.string, text, i);
        if (end < 0) { span('tk-str', text.slice(i)); return out.join(''); }
        span('tk-str', text.slice(i, end)); i = end; state.string = null; continue;
      }
      if (spec.line && text.startsWith(spec.line, i)) { span('tk-com', text.slice(i)); break; }
      if (spec.block && text.startsWith(spec.block[0], i)) {
        const close = text.indexOf(spec.block[1], i + spec.block[0].length);
        if (close < 0) { span('tk-com', text.slice(i)); state.block = true; break; }
        const end = close + spec.block[1].length;
        span('tk-com', text.slice(i, end)); i = end; continue;
      }
      const string = spec.strings.find(s => text.startsWith(s.open, i));
      if (string) {
        const end = findStringEnd(string, text, i + string.open.length);
        if (end < 0) { span('tk-str', text.slice(i)); state.string = string.open.includes(string.close) && string.multiline ? string : null; break; }
        span('tk-str', text.slice(i, end)); i = end; continue;
      }
      if (spec.regex && text[i] === '/' && /[(,=:[!&|?{};]\s*$/.test(text.slice(0, i))) {
        const end = findStringEnd({ close: '/', escape: true }, text, i + 1);
        if (end > 0) { span('tk-regex', text.slice(i, end)); i = end; continue; }
      }
      NUMBER.lastIndex = i;
      const number = NUMBER.exec(text);
      if (number && /^[\d]/.test(number[0].replace(/^0[xXbBoO]/, ''))) {
        span('tk-num', number[0]); i += number[0].length; continue;
      }
      IDENT.lastIndex = i;
      const ident = IDENT.exec(text);
      if (ident) {
        const word = ident[0];
        const before = text.slice(0, i).trimEnd();
        const after = text.slice(i + word.length);
        const isProperty = spec.properties ? /^["']?\s*:/.test(after) : before.endsWith('.');
        if (spec.keywords.has(word)) span('tk-kw', word);
        else if (spec.constants.has(word)) span('tk-const', word);
        else if (spec.control.has(word)) span('tk-ctl', word);
        else if (/^\s*\(/.test(after)) {
          const previous = (before.match(/[\w$]+$/) || [''])[0];
          span(previous === 'new' || previous === 'typeof' ? 'tk-type' : 'tk-fn', word);
        }
        else if (isUpperStart(word)) span('tk-type', word);
        else if (isProperty) span('tk-prop', word);
        else plain += word;
        i += word.length; continue;
      }
      plain += text[i];
      i++;
    }
    flush();
    return out.join('');
  }

  function scanJson(state, text) {
    const out = [];
    let plain = '';
    let i = 0;
    const flush = () => { if (plain) { out.push(escapeHtml(plain)); plain = ''; } };
    const span = (cls, value) => { flush(); out.push(`<span class="${cls}">${escapeHtml(value)}</span>`); };
    while (i < text.length) {
      if (state.string) {
        const end = findStringEnd(state.string, text, i);
        if (end < 0) { span('tk-str', text.slice(i)); return out.join(''); }
        const value = text.slice(i, end);
        // A string followed by a colon is an object key.
        span(/^\s*:/.test(text.slice(end)) ? 'tk-prop' : 'tk-str', value);
        i = end; state.string = null; continue;
      }
      if (text[i] === '"') {
        const end = findStringEnd({ close: '"', escape: true }, text, i + 1);
        if (end < 0) { span('tk-str', text.slice(i)); state.string = { close: '"', escape: true }; break; }
        const value = text.slice(i, end);
        span(/^\s*:/.test(text.slice(end)) ? 'tk-prop' : 'tk-str', value);
        i = end; continue;
      }
      if (text.startsWith('//', i)) { span('tk-com', text.slice(i)); break; }
      if (text.startsWith('/*', i)) {
        const close = text.indexOf('*/', i + 2);
        if (close < 0) { span('tk-com', text.slice(i)); state.block = true; break; }
        span('tk-com', text.slice(i, close + 2)); i = close + 2; continue;
      }
      if (state.block) {
        const close = text.indexOf('*/', i);
        if (close < 0) { span('tk-com', text.slice(i)); return out.join(''); }
        span('tk-com', text.slice(i, close + 2)); i = close + 2; state.block = false; continue;
      }
      const literal = /^(true|false|null)\b/.exec(text.slice(i));
      if (literal) { span('tk-const', literal[0]); i += literal[0].length; continue; }
      const number = /^-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?/.exec(text.slice(i));
      if (number) { span('tk-num', number[0]); i += number[0].length; continue; }
      plain += text[i];
      i++;
    }
    flush();
    return out.join('');
  }

  function scanMarkup(state, text) {
    const out = [];
    let plain = '';
    let i = 0;
    const flush = () => { if (plain) { out.push(escapeHtml(plain)); plain = ''; } };
    const span = (cls, value) => { flush(); out.push(`<span class="${cls}">${escapeHtml(value)}</span>`); };
    while (i < text.length) {
      if (state.block) {
        const close = text.indexOf('-->', i);
        if (close < 0) { span('tk-com', text.slice(i)); return out.join(''); }
        span('tk-com', text.slice(i, close + 3)); i = close + 3; state.block = false; continue;
      }
      if (text.startsWith('<!--', i)) {
        const close = text.indexOf('-->', i + 4);
        if (close < 0) { span('tk-com', text.slice(i)); state.block = true; break; }
        span('tk-com', text.slice(i, close + 3)); i = close + 3; continue;
      }
      if (text[i] === '<') {
        const doctype = /^<[!?][^>]*>/.exec(text.slice(i));
        if (doctype) { span('tk-pre', doctype[0]); i += doctype[0].length; continue; }
        const close = text.indexOf('>', i);
        const tagText = close < 0 ? text.slice(i) : text.slice(i, close + 1);
        const match = /^(<\/?)([\w:.-]+)/.exec(tagText);
        if (match) {
          span('tk-pun', match[1]);
          span('tk-tag', match[2]);
          // Attributes: name, =, quoted or bare value. Whitespace is preserved
          // as plain text so the row still round-trips byte for byte.
          let cursor = match[0].length;
          while (cursor < tagText.length) {
            const rest = tagText.slice(cursor);
            const attribute = /^([\w:.-]+)(\s*=\s*)("[^"]*"|'[^']*'|[^\s>]+)?/.exec(rest);
            if (attribute) {
              span('tk-attr', attribute[1]);
              span('tk-pun', attribute[2]);
              if (attribute[3]) span('tk-str', attribute[3]);
              cursor += attribute[0].length;
              continue;
            }
            const end = /^\/?>/.exec(rest);
            if (end) { span('tk-pun', end[0]); cursor += end[0].length; continue; }
            plain += rest[0];
            cursor++;
          }
          i += tagText.length;
          continue;
        }
      }
      const entity = /^&(#\d+|#x[\da-fA-F]+|\w+);/.exec(text.slice(i));
      if (entity) { span('tk-esc', entity[0]); i += entity[0].length; continue; }
      plain += text[i];
      i++;
    }
    flush();
    return out.join('');
  }

  function scanCss(state, text) {
    const out = [];
    let plain = '';
    let i = 0;
    const flush = () => { if (plain) { out.push(escapeHtml(plain)); plain = ''; } };
    const span = (cls, value) => { flush(); out.push(`<span class="${cls}">${escapeHtml(value)}</span>`); };
    while (i < text.length) {
      if (state.block) {
        const close = text.indexOf('*/', i);
        if (close < 0) { span('tk-com', text.slice(i)); return out.join(''); }
        span('tk-com', text.slice(i, close + 2)); i = close + 2; state.block = false; continue;
      }
      if (text.startsWith('/*', i)) {
        const close = text.indexOf('*/', i + 2);
        if (close < 0) { span('tk-com', text.slice(i)); state.block = true; break; }
        span('tk-com', text.slice(i, close + 2)); i = close + 2; continue;
      }
      if (text[i] === '"' || text[i] === "'") {
        const end = findStringEnd({ close: text[i], escape: true }, text, i + 1);
        const stop = end < 0 ? text.length : end;
        span('tk-str', text.slice(i, stop)); i = stop; continue;
      }
      const atRule = /^@[\w-]+/.exec(text.slice(i));
      if (atRule) { span('tk-kw', atRule[0]); i += atRule[0].length; continue; }
      const custom = /^--[\w-]+/.exec(text.slice(i));
      if (custom) { span('tk-prop', custom[0]); i += custom[0].length; continue; }
      const property = /^([a-zA-Z-]+)(\s*:)/.exec(text.slice(i));
      if (property) {
        span('tk-prop', property[1] + property[2]); i += property[0].length; continue;
      }
      const number = /^-?\d+(?:\.\d+)?(?:px|em|rem|vh|vw|%|s|ms|fr|deg|ch|ex|pt)?/.exec(text.slice(i));
      if (number) { span('tk-num', number[0]); i += number[0].length; continue; }
      const selector = /^[.#]?[\w-]+(?=[^;{}]*\{)/.exec(text.slice(i));
      if (selector) { span('tk-type', selector[0]); i += selector[0].length; continue; }
      const ident = IDENT;
      ident.lastIndex = i;
      const word = ident.exec(text);
      if (word) { plain += word[0]; i += word[0].length; continue; }
      plain += text[i];
      i++;
    }
    flush();
    return out.join('');
  }

  function scanMarkdown(state, text) {
    const out = [];
    const span = (cls, value) => out.push(`<span class="${cls}">${escapeHtml(value)}</span>`);
    const fence = /^\s*(```|~~~)/.exec(text);
    if (state.fence) {
      if (fence) { state.fence = false; span('tk-pun', text); return out.join(''); }
      span('tk-str', text);
      return out.join('');
    }
    if (fence) { state.fence = true; span('tk-pun', text); return out.join(''); }
    const heading = /^(\s*#{1,6}\s+.*)$/.exec(text);
    if (heading) { span('tk-kw', text); return out.join(''); }
    const quote = /^(\s*>\s?.*)$/.exec(text);
    if (quote) { span('tk-com', text); return out.join(''); }
    // Inline code, links and emphasis, line by line.
    let i = 0;
    let plain = '';
    const flush = () => { if (plain) { out.push(escapeHtml(plain)); plain = ''; } };
    while (i < text.length) {
      const rest = text.slice(i);
      const code = /^`[^`]*`/.exec(rest);
      if (code) { flush(); span('tk-str', code[0]); i += code[0].length; continue; }
      const link = /^\[[^\]]*\]\([^)]*\)/.exec(rest);
      if (link) { flush(); span('tk-prop', link[0]); i += link[0].length; continue; }
      const bullet = /^\s*(?:[-*+]|\d+\.)\s/.exec(rest);
      if (bullet) { flush(); span('tk-pun', bullet[0]); i += bullet[0].length; continue; }
      plain += text[i];
      i++;
    }
    flush();
    return out.join('');
  }

  function scan(state, text) {
    switch (state.spec.mode) {
      case 'json': return scanJson(state, text);
      case 'markup': return scanMarkup(state, text);
      case 'css': return scanCss(state, text);
      case 'markdown': return scanMarkdown(state, text);
      default: return scanCode(state, text);
    }
  }

  const MAX_HIGHLIGHT_CHARS = 512000;

  // Colour every row for both sides. Rows are visited in file order per side so
  // multi-line comments and strings stay in step across collapsed context gaps.
  function tokenize(path, rows) {
    const result = { language: null, old: new Map(), new: new Map() };
    const spec = languageFor(path);
    if (!spec || !rows || !rows.length) return result;
    result.language = spec.mode;
    let total = 0;
    for (const row of rows) total += row.text.length;
    if (total > MAX_HIGHLIGHT_CHARS) return result;
    const oldState = createState(spec);
    const newState = createState(spec);
    for (const row of rows) {
      // Rows keep CRLF file endings; drop the CR so it is never rendered.
      const text = row.text.charCodeAt(row.text.length - 1) === 13 ? row.text.slice(0, -1) : row.text;
      if (row.oldLine !== null && row.oldLine !== undefined) result.old.set(row, scan(oldState, text));
      if (row.newLine !== null && row.newLine !== undefined) result.new.set(row, scan(newState, text));
    }
    return result;
  }

  globalThis.DiffReviewHighlight = { tokenize, languageFor, escapeHtml };
})();
