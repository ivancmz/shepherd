# ADR-0002: Entrega de mensajes y despertar del destinatario

- Estado: **Aceptada**. Los reintentos y recordatorios del aviso están ampliados por [ADR-0009](0009-reintentos-y-recordatorios.md)
- Fecha: 2026-11-13
- Relacionada: [ADR-0001](0001-wsl-como-nodo-tcp.md), spike en `spikes/channel-spike/`

## Contexto

El spike comprobó dos vías oficiales para meter un mensaje en una sesión que está parada:

- **Claude Code, Channels:** el servidor MCP declara `experimental['claude/channel']` y emite `notifications/claude/channel`. El texto llega a Claude dentro de `<channel source="…" …>`, marcado como externo y con atributos (`meta`) como el remitente. Despierta a la sesión parada y, si está ocupada, encola el mensaje. Claude Code no confirma la entrega. Exige arrancar Claude con `--dangerously-load-development-channels server:<nombre>`.
- **Codex, `codex queue --thread <id> --message <texto>`:** el texto entra como **turno de usuario**, con la misma autoridad que lo que escribe el usuario. Despierta a la sesión parada y, si está ocupada, encola el mensaje sin interrumpir. Necesita que el hilo exista, es decir, que haya habido un primer mensaje.

Escribir en la pantalla del agente (herdr `send-text`/`agent prompt`, tmux) funciona con Claude, pero con Codex es frágil: el primer Enter se pierde porque Codex detecta un pegado.

## Decisión

**Entrega mixta, distinta según el agente:**

| Destino | Qué se empuja | Cómo lee el contenido |
|---|---|---|
| Claude con canal | **El mensaje completo** por el canal, con `meta`: `sender`, `sender_agent`, `node`, `message_id`, `thread_id` | Ya lo tiene. Responde con `reply`/`send` |
| Codex | **Solo un aviso fijo** por `codex queue`: `[shepherd] Tienes N mensaje(s) nuevo(s) de <remitente>. Léelos con la herramienta read_inbox.` | `read_inbox` (resultado de herramienta = datos, no órdenes) |
| Claude sin canal, en herdr | Aviso fijo corto por `herdr agent prompt` | `read_inbox` |
| Claude sin canal, fuera de herdr | Nada. El mensaje espera en el buzón | `read_inbox` cuando el agente lo llame |

- **Quién despierta:** el broker de Windows.
  - Codex de Windows: `codex queue` directamente.
  - Codex de WSL: `wsl -e bash -ic 'codex queue …'` (no `-lc`; ver ADR-0001).
  - Claude con canal, local o en WSL: notificación por la conexión de su shim con el broker (pipe local o TCP de ADR-0001).
- **El aviso a Codex no lleva contenido escrito por el remitente.** Solo el nombre del remitente registrado en el broker, filtrado y de longitud limitada, y el número de mensajes. Así ningún agente puede darle órdenes a Codex con la autoridad del usuario.
- **Destinatario ocupado:** no se interrumpe. Las dos vías encolan y lo entregan al terminar el turno.
- **Agrupar avisos:** si llegan varios mensajes a un Codex antes de que lea, se envía un único aviso (por destinatario), no uno por mensaje.
- **Buzón siempre:** todo mensaje se guarda en el buzón del broker antes de intentar despertar a nadie. El aviso es una ayuda: si falla, el mensaje no se pierde.
- **Pantalla como último recurso:** escribir en la pantalla del agente solo se usa con Claude sin canal dentro de herdr, nunca para el contenido, y nunca con Codex.

## Alternativas descartadas

- **Contenido completo también por `codex queue`:** ahorra una llamada a herramienta, pero cualquier agente podría darle órdenes a Codex con la autoridad del usuario.
- **Solo aviso también en Claude:** más uniforme, pero el canal ya marca el contenido como externo y con remitente. Ahorrar la ida y vuelta compensa.

## Consecuencias

- El shim tiene que ofrecer como mínimo `read_inbox`, `send` y `reply`, y en Claude declarar el canal.
- Hace falta un **acuse de lectura** propio: Claude Code no confirma la entrega por canal y `codex queue` solo confirma que el mensaje quedó encolado. El mensaje pasa a "leído" cuando el destinatario llama a `read_inbox`, o cuando el canal lo entrega y el agente responde. El detalle va en el ADR del modelo de mensaje.
- Para despertar a Codex el broker necesita el `thread_id` de cada Codex registrado. Lo obtiene del `_meta` de la primera llamada a herramienta, o del registro de herdr o de `state_5.sqlite` (ADR de identidad).
- Un Codex recién arrancado no se puede despertar hasta su primer mensaje. El lanzador puede enviarle un primer mensaje de presentación.
- Si Anthropic cambia los Channels (siguen en vista previa de investigación), solo cambia la entrega a Claude: el buzón y el aviso por herdr siguen sirviendo como alternativa.
