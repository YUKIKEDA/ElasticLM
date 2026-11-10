@AGENTS.md

Claude Code does not read `.cursor/rules/` on its own. `AGENTS.md` lists the rules. Read the ones that match the work before you change anything, and always read `workflow`, `consent`, `git`, `scope`, and `security`.

After the conversation is summarized (context compaction), read them again before the next change: the summary does not carry the rules. In V1c5 the first commit after a summary used `git commit -F -` with a here-string, which `powershell-shell` already rules out (PR #304's retrospective).
