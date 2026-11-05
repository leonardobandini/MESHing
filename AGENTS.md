# MESHing repository instructions

For every Git commit in this repository, use the verified GitHub identity
`leonardobandini <86308008+leonardobandini@users.noreply.github.com>` for both
author and committer. Supply `-c user.name=leonardobandini` and
`-c user.email=86308008+leonardobandini@users.noreply.github.com` to the
individual `git commit` command; verify the recorded author and committer
before pushing. Do not change the machine's global Git configuration.

The local historical V2 references copied from the private predecessor
workspace are not part of the public release or Git commits unless the user
explicitly requests their publication.

At the start of the next session, read `docs/NEXT_SESSION.md` for the latest
handoff state before continuing release or implementation work.

When working from this MESHing directory, use the MCP server from the sibling
`../csi-fem-assistant` repository for supported CSI model/session queries and
operations. The local, Git-ignored `opencode.json` points OpenCode at that
repository's versioned server and Python environment; do not copy its MCP
sources into MESHing. Confirm the server's version and availability before
using its tools, and never claim an MCP call was made if it was unavailable.
