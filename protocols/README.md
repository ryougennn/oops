# Vendored protocol XML

Pinned copies; update deliberately and regenerate goldens (`UPDATE_GOLDENS=1 dotnet test --project tests/oopshell.Scanner.Tests`).

| Directory | Upstream | Pinned at |
|---|---|---|
| `wayland/wayland.xml` | https://gitlab.freedesktop.org/wayland/wayland | release 1.26.0 (copied from Fedora `wayland-devel-1.26.0`) |
| `wayland-protocols/*` | https://gitlab.freedesktop.org/wayland/wayland-protocols | release 1.49 (copied from Fedora `wayland-protocols-devel-1.49`) |
| `wlr-protocols/*` | https://gitlab.freedesktop.org/wlroots/wlr-protocols | commit `1106a4a1c57562ddd438c547da4c0d6dc150d624` |
| `wlroots/virtual-keyboard-unstable-v1.xml` | https://gitlab.freedesktop.org/wlroots/wlroots (`protocol/`) | tag 0.19.0, commit `b2a10ed7bcdd366acb20cb55327ea5df45b6b5b9` |

`ext-image-capture-source-v1.xml` and `ext-image-copy-capture-v1.xml` (staging) back `ScreenCapture` (window and output capture).
`tablet-v2.xml` is only here because `cursor-shape-v1` references `zwp_tablet_tool_v2`.
`xdg-output-unstable-v1.xml` gives exact logical output geometry (see docs/DECISIONS.md).
