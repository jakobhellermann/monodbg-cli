# monodbg

Simple CLI mono debugger.

```
usage: monodbg <cmd> [--host H] [--port N]
  break <Type.Method> [--asm N] [--wait] [--timeout S]   arm; --wait blocks until hit
  inspect [<expr>] [--frame N]                            no expr: list roots; expr: this | arg | this.field.sub
  bp | unbreak <#|Type.Method> | unbreak --all             list | remove armed breakpoints
  stack | continue | status | quit
```

Current state: completely vibecoded, for personal use. Might rewrite this later.


# Slop Readme

Minimal CLI client for a **Mono soft-debugger** agent (the same wire protocol Rider/MonoDevelop
use). Built to set breakpoints and inspect state in a running Unity game without a GUI debugger.

Primary target: **Hollow Knight** launched via Doorstop with the mono debug server enabled
(`run.sh`: `debug_enable=1`, `debug_address=127.0.0.1:10001`). Any `--debugger-agent` /
`server=y` agent works via `--host/--port`.

## Model

A background **daemon** holds the single VM connection and keeps it suspended at a breakpoint
between commands; thin CLI subcommands talk to it over a per-target unix socket
(`/tmp/monodbg-<host>-<port>.sock`). So you arm once, trigger the code in-game, and then poke at
the held stop with separate `inspect` calls — no re-arming from scratch.

- **One debugger at a time.** Detach Rider while the daemon is attached (the agent accepts one
  connection).
- Breakpoints are set at **method entry** (IL offset 0) — no PDB needed, works on the stripped
  game DLLs.
- **Field-only inspection** (no method/property invokes), so a suspended frame can't hang. Reads
  `this`, args, and dotted field paths one level deep.
- The whole VM is suspended while stopped (game frozen) until `continue` or `quit`.

## Build

```sh
dotnet build
```

The soft-debugger client itself has no NuGet package; its source is vendored from
[`mono/debugger-libs`](https://github.com/mono/debugger-libs) under `vendor/Mono.Debugger.Soft/`
(only its deps — `Mono.Cecil`, `Microsoft.SymbolStore`, `Microsoft.FileFormats` — come from NuGet).

## Usage

```sh
D="dotnet bin/Debug/net8.0/monodbg.dll"

# arm a breakpoint (daemon auto-spawns on first break); returns immediately
$D break HeroController.Downspike --asm Silksong.Assembly-CSharp

#   ... trigger it in-game; the hit freezes the game and holds the frame ...

$D status                        # state=Stopped at HeroController.Downspike()
$D stack                         # #0 Downspike  <-  #1 FixedUpdate
$D bp                            # list armed breakpoints (with indices)
$D unbreak 0                     # remove by index; also: unbreak Type.Method | unbreak --all
$D inspect this                  # all instance fields, one level flat
$D inspect this.cState           # -> downSpiking=True, falling=True, onGround=False, ...
$D inspect this.cState.onGround  # nested field path
$D continue                      # resume until the next hit (stays armed)
$D quit                          # detach: game runs, breakpoint + daemon gone
```

`break --wait` blocks the call until the hit (prints the stack), instead of returning
immediately — handy from a terminal, less so when you can't see the prompt during the block.

### Subcommands

| command | what it does |
| --- | --- |
| `break <Type.Method> [--asm N] [--wait] [--timeout S]` | arm a method-entry breakpoint; auto-spawns the daemon; re-arming is a no-op |
| `bp` | list armed breakpoints with indices |
| `unbreak <#\|Type.Method\|--all>` | remove breakpoint(s) by index, by name (all overloads), or all |
| `inspect [<expr>] [--frame N]` | list the frame's roots (no expr) or read `this` / arg / `this.field.sub` (fields only) |
| `stack` | current call stack |
| `continue` | resume the VM (breakpoint stays armed) |
| `status` | daemon state, armed breakpoint count, current location |
| `quit` | detach + stop the daemon (unfreezes the game) |

Global: `--host H` (default `127.0.0.1`), `--port N` (default `10001`).

`--asm NAME` disambiguates same-named types across assemblies. In HK+Silksong, `HeroController`
exists in both `Assembly-CSharp` (HK) and `Silksong.Assembly-CSharp` (Hornet) — without `--asm`,
`break` errors and lists the candidates rather than guessing.

## Design notes

- Fails fast: any type/method/connection problem exits non-zero instead of blocking.
- The daemon is spawned via `setsid` with redirected fds so it doesn't inherit the caller's
  stdio (else it holds the shell's pipe open).
- Known gap: property reads (`transform.position`, `rb2d.linearVelocity`) need method invokes,
  deliberately not done yet.

**Every little annoyance in daily use is worth fixing** — this tool is meant to get out of the
way. If a command is awkward, an output is noisy, or a common step needs too much typing, improve
it rather than working around it.
