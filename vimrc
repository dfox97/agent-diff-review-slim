" A small, self-contained Vim config.
"
" Needs stock Vim 8.0+ (the "vim" package on Ubuntu) and nothing else. The
" colours, netrw and the quickfix list all ship with Vim, and the only outside
" program used is grep, which every base Ubuntu install already has. Copy this
" to ~/.vimrc and it works as-is.
"
"   Space Space   find a file, Tab lists matches   Space g    grep the tree
"   Space f       find a file by exact name        Space *    grep word under cursor
"   Space F       find a file in a split           Space n/p  next / previous hit
"   Space e       file explorer sidebar            Space c    close the list
"   Space s / q   write / quit                     Space h    clear highlighting

set nocompatible
let mapleader = ' '

filetype plugin indent on
syntax enable

" Appearance: bundled dark theme that works in any terminal.
set background=dark
colorscheme desert
set number
set numberwidth=3
set cursorline
set nowrap
set scrolloff=8
set sidescrolloff=8
set laststatus=2
set showcmd
set ruler
set showmode
set signcolumn=yes
set statusline=%f\ %h%m%r%=%{&filetype}\ \ %l:%c\ \ %p%%

" Editing defaults. Filetype rules may override the indentation ones.
set tabstop=2
set softtabstop=2
set shiftwidth=2
set expandtab
set autoindent
set backspace=indent,eol,start
set hidden
set splitbelow
set splitright
set diffopt+=vertical
set wildmenu
set wildmode=longest:full,full
set wildignore+=*.o,*.pyc,*/node_modules/*,*/.git/*

" Search: highlight as you type; an uppercase letter makes it case-sensitive.
set hlsearch
set incsearch
set ignorecase
set smartcase
set showmatch

" 'path' drives :find, :sfind, gf and Ctrl-W f. ",," keeps the directory of the
" current file and "**" searches the whole tree, so ":find **/name" finds a
" file anywhere below the working directory.
set path=.,,**
set wildignorecase

" :grep shells out to grep: -r recurse, -n line numbers, -I skip binaries.
" 'wildignore' does not apply to :grep, so the noisy directories are listed
" here as well. With no file arguments GNU grep -r searches the working tree.
set grepprg=grep\ -rnI\ --exclude-dir=.git\ --exclude-dir=node_modules\ --exclude-dir=.venv\ --exclude-dir=dist\ --exclude-dir=build\ $*
set grepformat=%f:%l:%m

" Show the quickfix window once a search has results. :cwindow stays quiet when
" there is nothing to show, so a grep that finds nothing changes nothing.
augroup quickfix_autoopen
  autocmd!
  autocmd QuickFixCmdPost [^l]* cwindow
  autocmd QuickFixCmdPost l*    lwindow
augroup END

" Space Space finds a file: type any part of the name and press Tab, and the
" wildmenu lists every match with its full path. Space f is for when you know
" the exact name and want to press Enter straight away; Space F opens a split.
nnoremap <leader><leader> :find **/*
nnoremap <leader>f :find **/
nnoremap <leader>F :sfind **/
nnoremap <leader>g :grep!<Space>
nnoremap <leader>* :execute 'grep! ' . shellescape(expand('<cword>'))<CR>
nnoremap <silent> <leader>n :cnext<CR>
nnoremap <silent> <leader>p :cprevious<CR>
nnoremap <silent> <leader>c :cclose<CR>
nnoremap <silent> <leader>h :nohlsearch<CR>

" File explorer, from netrw which is bundled with Vim.
let g:netrw_banner = 0
let g:netrw_liststyle = 3
let g:netrw_winsize = 25
let g:netrw_browse_split = 4
let g:netrw_altv = 1
let g:netrw_keepdir = 1
nnoremap <silent> <leader>e :Lexplore<CR>
nnoremap <silent> <leader>E :Explore<CR>

" Windows.
nnoremap <silent> <leader>w :vsplit<CR>
nnoremap <silent> <leader>- :split<CR>
nnoremap <silent> <leader>= <C-w>=
nnoremap <silent> <C-h> <C-w>h
nnoremap <silent> <C-j> <C-w>j
nnoremap <silent> <C-k> <C-w>k
nnoremap <silent> <C-l> <C-w>l

" Files and buffers. :quit refuses to discard unsaved changes.
nnoremap <silent> <leader>s :write<CR>
nnoremap <silent> <leader>q :quit<CR>
nnoremap <silent> <leader>bb :ls<CR>:buffer<Space>
nnoremap <silent> <leader>bn :bnext<CR>
nnoremap <silent> <leader>bp :bprevious<CR>

" Keep the selection when changing indentation, and move lines with Alt-j / k.
" Terminal support for Alt varies, so these may do nothing in some terminals.
xnoremap < <gv
xnoremap > >gv
nnoremap <silent> <A-j> :move .+1<CR>==
nnoremap <silent> <A-k> :move .-2<CR>==
nnoremap <silent> <A-Up> :move .-2<CR>==
nnoremap <silent> <A-Down> :move .+1<CR>==

" Avoid suspending Vim by accident.
nnoremap <C-z> <Nop>
xnoremap <C-z> <Nop>

augroup extra_filetypes
  autocmd!
  autocmd BufRead,BufNewFile *.njk setfiletype html
augroup END

" Insert mode completion is built in: Ctrl-N / Ctrl-P for words,
" Ctrl-X Ctrl-F for file names.
