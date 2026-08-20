# Grammars

`fsharp.tmLanguage.json` is Ionide's F# TextMate grammar — the one VS Code
itself ships — vendored here and embedded in the assembly. MIT licensed, from
[ionide/ionide-fsgrammar](https://github.com/ionide/ionide-fsgrammar) by way of
[microsoft/vscode](https://github.com/microsoft/vscode/blob/main/extensions/fsharp/syntaxes/fsharp.tmLanguage.json),
at ionide commit `0cb968a`.

**Vendored rather than taken from `TextMateSharp.Grammars`**, which is the usual
way to get it. That package carries 151 grammars in a single 6.75 MB assembly to
supply the one this debugger reads, and every byte the debugger references is
downloaded by anyone who opens the browser tutorial. `TextMateSharp` itself —
the engine that reads this file — is 143 KB.

To update: replace the file, and check `Highlight.fs` still recognises the
scopes it maps. Nothing else here depends on it.
