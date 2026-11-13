 ```bash
   # Git branch support (Ubuntu/Debian path).
   if [ -f /usr/lib/git-core/git-sh-prompt ]; then
     source /usr/lib/git-core/git-sh-prompt
   fi

   # Prompt: green user@host, blue folder, yellow Git branch.
   PS1='\[\033[01;32m\]\u@\h\[\033[00m\]:\[\033[01;34m\]\w\[\033[01;33m\]$(__git_ps1 " (%s)")\[\033[00m\]\$ '

   # Coloured file listings.
   if command -v dircolors >/dev/null 2>&1; then
     if [ -r ~/.dircolors ]; then
       eval "$(dircolors -b ~/.dircolors)"
     else
       eval "$(dircolors -b)"
     fi
   fi

   alias ls='ls --color=auto'
   alias ll='ls -alF'
   alias la='ls -A'

   # Highlight matching text in search results.
   alias grep='grep --color=auto'
 ```

 Apply it after saving:

 ```bash
   source ~/.bashrc
 ```
