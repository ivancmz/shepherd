# shepherd

A message bus between coding agents. It lets several **Claude Code** and **Codex** sessions (on Windows and in WSL) message each other by name, wake each other up and talk to you too, without typing into another agent's terminal or using the clipboard.

```
  lead   (Claude, Windows) ──┐
  dev    (Codex,  Windows) ──┼── broker (Windows) ── you: shepherd send / inbox
  tester (Claude, WSL) ──────┘        │
                                      └─ SQLite mailbox + audit log
```

- **Readable addresses:** `dev`, `reviewer`, `tester@wsl-ubuntu`.
- **Messages are never lost:** each one is stored in the mailbox before anyone is notified.
- **Wakes the recipient** even when it is idle, and does not interrupt it when it is busy.
- **Requests with answers:** a `request` stays pending until it is answered with `reply`.
- **A single native executable** (`shepherd` / `shepherd.exe`), with no .NET runtime to install.

## Contents

- [Requirements](#requirements)
- [Installation](#installation)
- [Getting started](#getting-started)
- [Using it from the terminal](#using-it-from-the-terminal)
- [Using it from the agents](#using-it-from-the-agents-mcp-tools)
- [Examples](#examples)
- [Names and addresses](#names-and-addresses)
- [How a message is delivered](#how-a-message-is-delivered)
- [WSL](#wsl)
- [Configuration](#configuration)
- [Troubleshooting](#troubleshooting)
- [Working alongside herdr](#working-alongside-herdr)
- [Uninstalling](#uninstalling)
- [Development](#development)
- [License](#license)

## Requirements

| To | You need |
|---|---|
| Use it | Windows 10/11. Claude Code and/or Codex installed. WSL is optional |
| Build it on Windows | .NET 10 SDK and Visual Studio with "Desktop development with C++" (NativeAOT links with MSVC) |
| Build it in WSL | .NET 10 SDK, `clang` and `zlib1g-dev` (`sudo apt install clang zlib1g-dev`) |

The broker always runs on Windows. WSL agents connect to it.

## Installation

**1. Get the executable.** Download `shepherd-<version>-win-x64.zip` from the [Releases](https://github.com/ivancmz/shepherd/releases) page and unzip it (`shepherd.exe` and `e_sqlite3.dll` must stay together). `SHA256SUMS.txt` on the same page has the checksums.

Or build it from source (PowerShell, at the repo root):

```powershell
pwsh scripts/publish.ps1          # produces publish\win-x64\shepherd.exe
```

The commands below assume you built it; with the release, run `shepherd.exe` from the folder you unzipped.

**2. See what will change**, without touching anything:

```powershell
.\publish\win-x64\shepherd.exe setup --dry-run
```

**3. Install:**

```powershell
.\publish\win-x64\shepherd.exe setup --autostart
```

`setup` does the following, and asks for confirmation first (`--yes` skips the question):

- Copies the executable to `%LOCALAPPDATA%\shepherd\bin\<version>-<hash>\` and puts `shepherd` on your user PATH (open a new terminal to use it).
- Registers the `shepherd` MCP server with Claude Code (`claude mcp add -s user`) and Codex (`codex mcp add`).
- In Codex, pre-approves the shepherd tools, and only those.
- Adds a short rule to `~/.claude/CLAUDE.md` and `~/.codex/AGENTS.md`, between the `<!-- shepherd:begin -->` and `<!-- shepherd:end -->` markers, so agents use shepherd to talk to each other.
- Adds a `Stop` hook to `~/.claude/settings.json` that runs `shepherd wait` in the background (`asyncRewake`), so an idle Claude session is woken when mail arrives. Your other settings and hooks are kept; the file may be re-indented.
- With `--autostart`, starts the broker when you log on to Windows.

Every file it edits is first copied to `*.bak-shepherd-<date>`. If Claude or Codex is not installed, that step is skipped.

**4. WSL (optional).** WSL has its own configuration, so repeat the steps inside the distro, with `shepherd-<version>-linux-x64.tar.gz` from the Releases page or built from source:

```bash
tar -xzf shepherd-<version>-linux-x64.tar.gz && ./shepherd setup
# or, from source:
scripts/publish.sh                 # produces publish/linux-x64/shepherd
publish/linux-x64/shepherd setup
```

If WSL agents cannot connect, see [WSL](#wsl).

## Getting started

Open two terminals and start a named agent in each:

```powershell
# Terminal 1
shepherd launch claude --name lead

# Terminal 2
shepherd launch codex --name dev --prompt "You are dev. You work with lead; talk to them through shepherd."
```

From a third terminal, check that they are on the bus and write to them:

```powershell
shepherd agents
shepherd send dev "Review the date parser and tell lead what you find"
```

From here on you can ask `lead`, in its own chat, to "ask dev how it's going": it will use the `send` tool and the answer will reach it on its own.

> Starting Claude with `launch` shows a full-screen development channels warning. Accept it: it is what lets shepherd wake that session. Claude Code channels are in preview, so the warning appears on every start.

## Using it from the terminal

From the terminal you write as the **human user**: agents see you under your OS user name.

| Command | What it does |
|---|---|
| `shepherd setup [--dry-run] [--yes] [--autostart] [--undo]` | Installs and registers shepherd for Claude Code and Codex |
| `shepherd launch claude\|codex [--name <n>] [--prompt <text>] [--auto-confirm] [-- <args>]` | Starts an agent connected to the bus |
| `shepherd send <to>[,<to>...] <text> [--request] [--subject <s>]` | Sends a message |
| `shepherd inbox [--all] [--limit <n>]` | Reads your messages and marks them as read |
| `shepherd reply <message_id> <text>` | Answers a message |
| `shepherd agents` | Lists the agents |
| `shepherd status` | Broker status and log locations |
| `shepherd broker run\|start\|stop` | Runs the broker in the foreground, in the background, or stops it |
| `shepherd firewall` | Shows the Windows Firewall rule WSL needs (does not apply it) |
| `shepherd version` | Version |
| `shepherd mcp` | MCP server over stdio. Claude and Codex start it, not you |
| `shepherd wait` | Claude Code Stop hook. Waits in the background while the session is idle and wakes it when mail arrives. Claude starts it, not you |

You do not need to start the broker by hand: the first command or agent that needs it starts it.

`send`, `reply` and `inbox` act as you, so they refuse to run inside an agent's shell (Claude Code or Codex, including `! shepherd send` typed at the Claude prompt). Agents use the MCP tools instead.

### `launch` options

| Option | Effect |
|---|---|
| `--name <n>` | The agent's name on the bus. Without it the agent gets an automatic one |
| `--prompt <text>` | First message to the agent. In Codex it creates the thread, and a Codex without a thread cannot be woken |
| `--auto-confirm` | Claude inside [herdr](#working-alongside-herdr) only: accepts the channels warning. It never answers the folder-trust question |
| `-- <args>` | Everything after it is passed as is to `claude` or `codex` |

## Using it from the agents (MCP tools)

Each agent sees these tools from the `shepherd` server:

| Tool | Parameters | Purpose |
|---|---|---|
| `send` | `to` (list), `body`, `kind` (`note` \| `request`), `subject`, `refs` | Send a message to one or more agents, or to the user |
| `reply` | `message_id`, `body`, `refs` | Answer in the same thread. Notifies the sender |
| `read_inbox` | `unread_only` (default `true`), `limit` (default 20, max 100) | Read messages, oldest first. Marks them as read |
| `list_agents` | — | See who is there: name, node, kind, online state, unread messages, pending requests |
| `register` | `name` | Choose a name on the bus. Fails if another live session uses it |
| `whoami` | — | Show your own name and address |

Useful details:

- **`note` or `request`.** Use `request` when you expect an answer: it shows as pending in `status` and `list_agents` until the recipient calls `reply`.
- **`refs`** are file references (`path` and, optionally, `lines: "10-42"`), up to 20 per message. The file is not copied, and the path is translated between Windows and WSL for the recipient (`C:\repo\a.cs` ↔ `/mnt/c/repo/a.cs`).
- **Limits:** body up to 64 KiB (Markdown), subject up to 120 characters. Put large content in a file and pass it through `refs`.
- **An agent does not choose its sender.** It is not a parameter: shepherd derives it from the calling session. This guards against mistakes and injected prompts, not against a hostile process: every agent runs under your OS account, so it is best effort, not a security boundary.

You do not have to teach the agent the tools. Plain language is enough: "ask `reviewer` through shepherd whether the change looks right".

## Examples

### Send a note

```console
> shepherd send dev "The fix/dates branch is pushed"
Sent to dev@pc-mypc (01a1e109-e43b-72f0-b2af-9d735381a29a).
```

The identifier in parentheses is the `message_id`: use it to answer with `reply`.

### Ask for something and wait for the answer

```console
> shepherd send reviewer "Can you review src/Parser.cs before 5?" --request --subject "Parser review"
Sent to reviewer@pc-mypc (01a1e10a-25c1-7698-a9a9-ead86c15d923).
```

When `reviewer` answers, it shows up in your inbox:

```console
> shepherd inbox
1 message(s) via shepherd ...

---
message_id: 01a1e10a-c16e-770b-a7f1-34f03edd820c
from: reviewer@pc-mypc
kind: reply
thread: 01a1e10a-25c1-7698-a9a9-ead86c15d923 (in reply to 01a1e10a-25c1-7698-a9a9-ead86c15d923)
sent: 2026-11-16 10:42:07Z
subject: Re: Parser review

Reviewed. Two minor comments, nothing blocking.

---
Remaining unread: 0.
```

### Answer an agent that asks you something

```console
> shepherd inbox
...
message_id: 01a1e10b-7f3a-7c21-8e4d-5b0a9c3d1e62
from: dev@pc-mypc
kind: request (expects a reply)
...
Should I keep compatibility with the old format or drop it?

> shepherd reply 01a1e10b-7f3a-7c21-8e4d-5b0a9c3d1e62 "Keep it for one more release"
Reply sent to dev@pc-mypc (01a1e10b-9d04-7a55-b1c8-2f6e7a4b8d90).
```

### Send to several at once, including WSL

```console
> shepherd send dev,reviewer,tester@wsl-ubuntu "We freeze main at 18:00"
```

### See who is on the bus

```console
> shepherd agents
- api-claude@pc-mypc [claude, online, automatic name] cwd C:\repos\api
- dev@pc-mypc [codex, online] cwd C:\repos\api
- lead@pc-mypc [claude, online, 2 unread] cwd C:\repos\api — waiting for a reply from dev@pc-mypc
- my-user@pc-mypc [human, online]
- tester@wsl-ubuntu [claude, offline] cwd /mnt/c/repos/api
```

### A team of three agents

```powershell
shepherd launch claude --name lead     --prompt "You are lead. You coordinate dev and reviewer through shepherd."
shepherd launch codex  --name dev      --prompt "You are dev. You implement what lead asks. Ask reviewer for a review through shepherd."
shepherd launch claude --name reviewer --prompt "You are reviewer. You review what dev sends you and answer with reply."
```

Then, in `lead`'s chat:

> Ask dev to add date validation in `src/Parser.cs` and to let you know once reviewer has approved it.

What happens underneath:

1. `lead` calls `send(to: ["dev"], kind: "request", body: "…", refs: [{path: "src/Parser.cs"}])`.
2. shepherd wakes `dev`, which reads the task with `read_inbox`, does the work and sends a `request` to `reviewer`.
3. `reviewer` answers with `reply`; `dev` in turn answers `lead`.
4. You can follow all of it with `shepherd agents` and `shepherd status`.

### Pass arguments to the agent

```powershell
shepherd launch claude --name dev -- --model opus --continue
```

### An agent that was already open

Agents started without `launch` are on the bus too (the MCP server is registered globally), under an automatic name such as `api-claude`. To give one a better name, ask it: "register on shepherd as `dev`". It will use `register`.

What changes is how it gets woken: see the [delivery](#how-a-message-is-delivered) table.

## Names and addresses

- **Format:** lowercase letters, digits and dashes, 2 to 32 characters.
- **Address:** `name` within the same node, `name@node` from another.
- **Nodes:** `pc-<machine>` on Windows and `wsl-<distro>` in WSL, for example `reviewer@wsl-ubuntu`.
- **Automatic name:** `<folder>-<kind>[-n]`, for example `api-codex`. It is tagged `automatic name`.
- **A session keeps its name.** A session that comes back without asking for a name gets the one it had, automatic or chosen with `register`, along with its mailbox. A Codex session gets it back at its first tool call, when shepherd learns its thread; until then it has a provisional name.
- **A herdr pane keeps its name.** A new Claude session that opens in a herdr pane, in the same folder, takes over the name and mailbox of the pane's previous session once that one has gone.
- **Otherwise an automatic name is never handed to another session.** A new session gets the next free suffix. Automatic names that have been offline for 14 days with no unread mail are forgotten.
- **A name you choose belongs to the role.** If you close `dev` and start another session with `--name dev`, it inherits the mailbox and the pending messages.
- **No name stealing:** while a live session uses a name, nobody else can register it.
- Names are visible to every agent on the bus: do not put anything sensitive in them.

## How a message is delivered

Every message is stored in the mailbox first. Then shepherd tries to wake the recipient, and how it does that depends on the agent:

| Recipient | What it receives | How it reads the content |
|---|---|---|
| Claude started with `launch` (with a channel) | The full message, marked as external and with its sender | It already has it. It answers with `reply` |
| Codex with a thread | A fixed notice: "you have N new messages, read them with `read_inbox`" | `read_inbox` |
| Claude without a channel, idle after a turn | The same fixed notice, through the Stop hook that `setup` installs (`shepherd wait`). It wakes the session without typing into its terminal | `read_inbox` |
| Claude without a channel, busy or not yet through its first turn, inside herdr | A short fixed notice typed into its pane | `read_inbox` |
| Claude without a channel, busy or not yet through its first turn, outside herdr | Nothing yet. The notice arrives when its turn ends, and the result of its next shepherd tool call mentions the mail | `read_inbox` |

- **A busy recipient is not interrupted:** the notice is queued and arrives when its turn ends.
- **The notice to Codex carries no text from the sender.** In Codex that notice enters as a user turn, so it only contains the sender's name and the number of messages. No agent can give another one orders with your authority.
- **Several messages in a row** to the same Codex are grouped into a single notice.
- **If the notice fails,** it is retried after 2, 10 and 30 seconds and then every 2 minutes while the recipient stays connected. The message stays in the mailbox.
- **Unread reminders:** a recipient that was notified but has not read gets a reminder after 10 minutes and another after an hour, and one more when its session reconnects. A new message starts the count again.
- **Retention:** read or answered messages are deleted after 90 days. Unread ones do not expire. The audit log (`audit-<year>-<month>.jsonl`) keeps the full text of every message; each monthly file is deleted once its whole month is older than 90 days, so a message can stay there for up to about 120 days.

Messages from other agents are treated like a colleague's: they never override your instructions or the session's permissions.

## WSL

WSL is one more node on the bus. The Linux `shepherd` connects over TCP to the Windows broker:

- The broker listens only on the `vEthernet (WSL)` interface, port `47321`, never on all interfaces.
- Each connection presents a token that the broker keeps in your Windows profile. From WSL it is read through `/mnt/c/Users/<user>/AppData/Local/shepherd`.
- Start order does not matter: if WSL starts after the broker, the broker begins listening when the interface appears.

**If a WSL agent cannot connect**, it is almost always the Windows Firewall. This command shows the exact rule, limited to the port, the WSL interface and the executable; you apply it yourself from an elevated PowerShell:

```powershell
shepherd firewall
```

Other cases:

| Situation | Fix |
|---|---|
| WSL with `networkingMode=mirrored` | On Windows: `SHEPHERD_TCP=127.0.0.1:47321` and restart the broker |
| The Linux user differs from the Windows one and there are several profiles | In WSL: `SHEPHERD_WIN_HOME=/mnt/c/Users/<user>/AppData/Local/shepherd` |
| You do not want the TCP listener | On Windows: `SHEPHERD_TCP=off` |

## Configuration

Everything works with no configuration. These environment variables change the defaults:

| Variable | Where | Effect |
|---|---|---|
| `SHEPHERD_HOME` | Windows / WSL | Data directory. Defaults to `%LOCALAPPDATA%\shepherd` or `~/.local/share/shepherd` |
| `SHEPHERD_USER` | Terminal | Your name on the bus. Defaults to the OS user |
| `SHEPHERD_TCP` | Broker | Listener for WSL: `auto` (default), `off` or `address:port` |
| `SHEPHERD_WIN_HOME` | WSL | The Windows broker's data directory as seen from Linux |
| `SHEPHERD_BROKER_HOST` | WSL | Broker address, if the detected one does not work |
| `SHEPHERD_NODE` | Windows / WSL | Node name, instead of `pc-<machine>` / `wsl-<distro>` |
| `SHEPHERD_PIPE` | Windows | Broker pipe name. Defaults to `shepherd-<user>` |
| `SHEPHERD_CLAUDE`, `SHEPHERD_CODEX` | Windows / WSL | Path to `claude` or `codex` when they are not on PATH |
| `SHEPHERD_NAME` | Agent | The agent's name. `launch --name` sets it; you do not need to touch it |

## Troubleshooting

```console
> shepherd status
broker 0.1.0 on node pc-mypc, up since 2026-11-16 07:47:51
data: C:\Users\me\AppData\Local\shepherd
logs: ...\broker.log, ...\logs (shims), ...\audit-*.jsonl (messages)
agents: 3 online / 4 known
unread messages: 2, pending requests: 1
```

Inside the data directory:

| File | Contents |
|---|---|
| `broker.log` | Broker start-up, connections and notices |
| `logs\mcp-<date>.log` | One log per day for each agent's MCP server (kept for 14 days) |
| `audit-<year>-<month>.jsonl` | Record of the messages |
| `shepherd.db` | Mailboxes and names (SQLite) |

Common problems:

| Symptom | Cause and fix |
|---|---|
| `shepherd` is not recognised after `setup` | The new PATH only applies to new terminals: open another one |
| A Codex does not wake up | It has no thread yet. Start it with `--prompt` or write it a first message |
| A Claude does not wake up | It was started without `launch` and outside herdr: it has no channel. It will read the message when it calls `read_inbox` |
| `register` fails | Another live session uses that name. Check `shepherd agents` |
| The WSL agent does not show up | Firewall or network: see [WSL](#wsl) |
| `a broker is already running` | There is already a broker. Run `shepherd broker stop` before `broker run` |

## Working alongside herdr

shepherd does not need herdr (a terminal multiplexer for agents), but the two get along: herdr manages panes and starts agents, shepherd carries the messages. `shepherd launch` can be used as a pane's command, `--auto-confirm` accepts the channels warning inside a pane, and `list_agents` shows each agent's pane. The rule `setup` adds asks agents to use shepherd instead of `herdr agent prompt` to talk to each other.

## Uninstalling

```powershell
shepherd setup --undo
```

It removes the MCP registration from Claude and Codex, the rule in the global instructions, the PATH entry and the autostart. The binaries and the data stay in `%LOCALAPPDATA%\shepherd`: delete them by hand once no agent is using them. In WSL, run the same command inside the distro.

## Development

```powershell
dotnet build Shepherd.slnx
dotnet test  Shepherd.slnx
```

| Folder | Contents |
|---|---|
| `src/Shepherd.Protocol` | Messages, names, transport (pipe and TCP) and the broker client |
| `src/Shepherd.Broker` | Broker: SQLite mailbox, audit log, notices and path translation |
| `src/Shepherd.Mcp` | The MCP server each agent sees |
| `src/Shepherd.Cli` | The `shepherd` executable: CLI, `setup` and `launch` |
| `tests/Shepherd.Tests` | Tests |
| `scripts/` | `publish.ps1` (win-x64) and `publish.sh` (linux-x64). NativeAOT does not cross-compile between operating systems: each is built on its own |
| `docs/adr/` | Design decisions, numbered (in Spanish) |
| `docs/bus/diagramas.md` | State and sequence diagrams (in Spanish) |

The design decisions are in the ADRs:

| ADR | Topic |
|---|---|
| [0001](docs/adr/0001-wsl-como-nodo-tcp.md) | WSL as a node over TCP |
| [0002](docs/adr/0002-entrega-y-despertar.md) | Message delivery and waking the recipient |
| [0003](docs/adr/0003-identidad-y-nombres.md) | Identity and names |
| [0004](docs/adr/0004-broker-local.md) | Local broker |
| [0005](docs/adr/0005-protocolo-de-nodo.md) | Node protocol |
| [0006](docs/adr/0006-modelo-de-mensaje.md) | Message model |
| [0007](docs/adr/0007-lanzador-setup-y-convivencia-herdr.md) | Launcher, `setup` and working alongside herdr |
| [0008](docs/adr/0008-el-nombre-sigue-a-la-sesion.md) | The name follows the session |
| [0009](docs/adr/0009-reintentos-y-recordatorios.md) | Unlimited retries and reminders of unread mail |
| [0010](docs/adr/0010-la-cli-no-firma-desde-un-agente.md) | The CLI does not send or read as the user from inside an agent |
| [0011](docs/adr/0011-conservacion-de-la-auditoria.md) | Audit log retention |
| [0012](docs/adr/0012-despertar-a-claude-con-el-hook-stop.md) | Waking Claude with the `Stop` hook |
| [0013](docs/adr/0013-el-panel-de-herdr-conserva-el-nombre.md) | A new session in a herdr pane inherits its name |

## License

[MIT](LICENSE)
