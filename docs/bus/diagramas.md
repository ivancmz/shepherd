# Bus de mensajes: diagramas

Diagramas de estados y de secuencia de la fase 1. Las decisiones que reflejan están en `docs/adr/0001` a `0007`.

## 1. Ciclo de vida de un mensaje (por destinatario)

Cada destinatario de un mensaje tiene su propio estado (ADR-0006). El aviso depende del tipo de agente (ADR-0002).

```mermaid
stateDiagram-v2
    [*] --> queued: send / reply<br/>(guardado en SQLite)

    queued --> notified: Claude con canal<br/>message.new → notifications/claude/channel
    queued --> notified: Codex con hilo<br/>codex queue (aviso fijo)
    queued --> notified: Claude sin canal en herdr<br/>herdr agent prompt (aviso fijo)
    queued --> queued: no se puede despertar<br/>(Claude sin canal fuera de herdr,<br/>Codex sin hilo, desconectado)

    queued --> read: read_inbox
    notified --> read: read_inbox<br/>o canal entregado y el agente actúa
    notified --> queued: el aviso falló<br/>(se reintenta con espera creciente)

    read --> answered: reply en el mismo hilo
    read --> [*]: note: no espera respuesta
    answered --> [*]

    read --> purged: 90 días
    answered --> purged: 90 días
    purged --> [*]
```

Notas:

- Si llegan varios mensajes a un Codex antes de que lea, se agrupan en un único aviso.
- Una `request` en `read` sin respuesta aparece como pendiente en `status` y `list_agents`.
- Los mensajes no leídos (`queued`, `notified`) no caducan.

## 2. Sesión de un agente vista por el broker

```mermaid
stateDiagram-v2
    [*] --> connected: hello (shim)
    connected --> registered: register(nombre)<br/>o nombre del lanzador
    connected --> registered: nombre automático<br/>(carpeta-tipo)
    connected --> rejected: nombre en uso<br/>por una sesión viva
    rejected --> connected: register(otro nombre)

    state registered {
        [*] --> unwakeable
        unwakeable --> wakeable: Claude con canal declarado
        unwakeable --> wakeable: Codex: thread_id visto en _meta<br/>de un tools/call
        unwakeable --> wakeable_herdr: Claude sin canal<br/>con HERDR_PANE_ID
    }

    registered --> disconnected: conexión cerrada<br/>o 45 s sin latido
    disconnected --> registered: misma identidad técnica<br/>reconecta (recupera nombre)
    disconnected --> released: el nombre queda libre<br/>(buzón conservado)
    released --> registered: otra sesión registra<br/>ese nombre (hereda buzón)
    registered --> [*]: el agente termina
```

## 3. Conexión del shim con el broker

Igual por named pipe (Windows) y por TCP (WSL), según ADR-0005.

```mermaid
stateDiagram-v2
    [*] --> locating
    locating --> spawning: Windows: no hay broker
    spawning --> connecting: broker listo (≤ 3 s)
    spawning --> failed: tiempo agotado
    locating --> connecting: hay broker<br/>(o WSL: dirección configurada)

    connecting --> greeting: conectado
    connecting --> backoff: error de conexión

    greeting --> registering: hello aceptado
    greeting --> failed: protocol_mismatch<br/>o unauthorized

    registering --> ready: register ok
    registering --> ready: name_taken → nombre automático<br/>y aviso al agente

    ready --> ready: ping/pong cada 15 s
    ready --> backoff: 45 s sin latido<br/>o conexión cerrada

    backoff --> locating: espera 0,5 s → 30 s máx.

    failed --> [*]: las herramientas devuelven<br/>un error claro al agente
```

## 4. Secuencia: lider (Claude en herdr, Windows) pide una revisión a dev (Codex en WSL)

```mermaid
sequenceDiagram
    autonumber
    participant L as lider<br/>(Claude, Windows)
    participant SL as shim lider
    participant B as broker<br/>(Windows)
    participant SD as shim dev<br/>(Linux)
    participant D as dev<br/>(Codex, WSL)

    L->>SL: send(to="dev@wsl-ubuntu", kind=request,<br/>body, refs=[C:\repo\src\x.c])
    SL->>B: send (pipe). from = lider@pc-x (lo pone el shim)
    B->>B: guarda en SQLite (queued)<br/>traduce ref → /mnt/c/repo/src/x.c
    B-->>SL: message_id, thread_id
    SL-->>L: ok
    B->>D: wsl -e bash -ic 'codex queue --thread <id><br/>--message "[shepherd] 1 mensaje de lider…"'
    Note over B,D: notified. Codex parado → empieza un turno
    D->>SD: read_inbox()
    SD->>B: read_inbox (TCP + token)
    B-->>SD: mensaje (estado → read)
    SD-->>D: mensaje como resultado de herramienta (datos)
    D->>D: revisa /mnt/c/repo/src/x.c
    D->>SD: reply(message_id, body)
    SD->>B: reply. from = dev@wsl-ubuntu
    B->>B: guarda (answered para dev, queued para lider)
    B->>SL: message.new (pipe)
    SL->>L: notifications/claude/channel<br/>sender=dev@wsl-ubuntu, kind=reply
    Note over L: Claude parado → empieza un turno<br/>con la respuesta ya en contexto
```
