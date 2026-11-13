# ADR-0007: Lanzador, configuración global y convivencia con herdr

- Estado: **Aceptada**
- Fecha: 2026-11-13
- Relacionadas: [ADR-0002](0002-entrega-y-despertar.md), [ADR-0003](0003-identidad-y-nombres.md), [ADR-0004](0004-broker-local.md)

## Contexto

Para que un agente esté en el bus:

- **Claude** necesita el servidor MCP `shepherd` y, para recibir avisos, arrancar con `--dangerously-load-development-channels server:shepherd`. Eso muestra una confirmación a pantalla completa en cada arranque.
- **Codex** necesita el servidor MCP, aprobar sus herramientas y tener un primer mensaje: sin hilo no se le puede despertar (ADR-0002).
- Cada agente necesita un nombre (ADR-0003).

Hoy los agentes usan `herdr agent prompt` para hablar entre ellos porque se lo indican tres fuentes:

1. Los prompts de presentación del usuario ("si tienes dudas, consulta a lider con herdr agent prompt …").
2. Memorias de proyecto de Claude que fijan `herdr agent prompt` como forma de pasar encargos a otro agente.
3. La propia CLI de herdr, que en cada `--help` remite a `herdr --skill`.

Las integraciones de herdr (`herdr integration`: hooks `herdr-agent-state.ps1` en Claude y Codex) solo informan del estado y del id de sesión, no envían mensajes.

## Decisión

### 1. Configuración global + lanzador

- **`shepherd setup`** (se ejecuta una vez en Windows y otra en WSL, porque son configuraciones distintas):
  - Registra el servidor MCP de usuario en Claude (`claude mcp add -s user shepherd -- shepherd mcp`) y en Codex (`[mcp_servers.shepherd]` en `~/.codex/config.toml`).
  - En Codex deja preaprobadas **solo** las herramientas de shepherd. Si la versión de Codex no lo admite por configuración, se aprueban una vez con "Always allow".
  - Añade la regla de convivencia (punto 2) a las instrucciones globales.
  - **Antes de escribir nada, muestra el diff y hace copia de seguridad** de cada fichero (`*.bak-AAAAMMDD`). `shepherd setup --undo` lo revierte.
- **`shepherd launch claude|codex --name <nombre> [--prompt "<texto>"] [--auto-confirm] [-- <args del agente>]`**:
  - Claude: añade `--dangerously-load-development-channels server:shepherd` y pasa el nombre al shim por entorno.
  - Codex: pasa el nombre con `-c mcp_servers.shepherd.env.SHEPHERD_NAME=<nombre>` (Codex no hereda el entorno en sus servidores MCP).
  - `--prompt`: el primer mensaje de presentación. En Codex crea el hilo al arrancar, así que ya se le puede despertar.
  - `--auto-confirm`: solo dentro de herdr (`HERDR_PANE_ID` presente). Pulsa el Enter de la confirmación de canales con `herdr pane send-keys` al detectarla. Fuera de herdr la confirma el usuario.
  - Funciona igual desde WT, WSL o `herdr agent start <nombre> --kind … --pane … -- …` con `shepherd launch` como comando. No requiere cambios en herdr.
- Un agente arrancado **sin** `launch` también está en el bus gracias al registro global. Puede enviar y leer, y se le despierta según ADR-0002 (Codex con hilo: sí; Claude sin canal: solo dentro de herdr).

### 2. Convivencia con herdr: que se use shepherd para los mensajes

| Capa | Qué | Cuándo |
|---|---|---|
| 1. El propio shepherd | Descripciones de `send`/`reply` ("úsala para hablar con otros agentes, en lugar de `herdr agent prompt` o el portapapeles") e `instructions` del servidor MCP | Desde el primer día |
| 2. Instrucciones globales | `setup` añade una regla breve a `~/.claude/CLAUDE.md` y `~/.codex/AGENTS.md` (Windows y WSL): *"Para hablar con otros agentes usa las herramientas de shepherd; `herdr agent prompt` solo si shepherd no está disponible."* | Al ejecutar `setup` |
| 3. Memorias y prompts | Actualizar, con permiso del usuario, las memorias que prescriben `herdr agent prompt`, y los prompts de presentación ("pregúntale a lider por shepherd") | Cuando shepherd funcione de verdad |
| 4. Hook de aviso | Regla en `~/.claude/hooks/guard_commands.py` que **avisa, sin bloquear**, cuando un agente usa `herdr agent prompt`, `herdr pane send-text` o `send-keys` hacia un panel de agente: "usa shepherd send" | Cuando shepherd funcione. Codex: no se sabe aún si sus hooks interceptan comandos antes de ejecutarse; de momento solo capas 1 y 2 |

herdr sigue siendo la herramienta para paneles, arrancar agentes, leer pantallas y ver estados. Sus integraciones de estado se mantienen: dan el cruce panel ↔ sesión que usa `list_agents`.

## Alternativas descartadas

- **Solo configuración global:** sin el flag de canales, Claude nunca recibe avisos fuera de herdr.
- **Solo lanzador:** los agentes arrancados a mano quedarían fuera del bus.
- **Hook que bloquea `herdr agent prompt` desde el principio:** demasiado agresivo mientras shepherd es nuevo. Puede romper los flujos que funcionan hoy si shepherd falla.

## Consecuencias

- `setup` modifica ficheros de configuración globales de Claude y Codex. Requiere diff previo, copia de seguridad y `--undo`.
- El flag `--dangerously-load-development-channels` seguirá mostrando la confirmación mientras los Channels estén en vista previa y shepherd no esté en una lista aprobada.
- Algunos usuarios de herdr en Windows ponen `daemon_auto_start = false` en Codex como solución temporal de herdrdev/herdr#4814. `codex queue` funcionó con esa configuración en el spike. El lanzador no debe cambiarla.
- Hay que comprobar si Codex entrega al modelo las `instructions` del servidor MCP. Si no, la capa 2 es la que cuenta.
