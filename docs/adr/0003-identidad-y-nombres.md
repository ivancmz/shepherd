# ADR-0003: Identidad y nombres de los agentes

- Estado: **Aceptada**. Los puntos 1 (nombre automático) y 3 están sustituidos por [ADR-0008](0008-el-nombre-sigue-a-la-sesion.md); el punto 5 está matizado por [ADR-0010](0010-la-cli-no-firma-desde-un-agente.md)
- Fecha: 2026-11-13
- Relacionadas: [ADR-0001](0001-wsl-como-nodo-tcp.md), [ADR-0002](0002-entrega-y-despertar.md)

## Contexto

- Los nombres de la mensajería nativa de Claude salen de la carpeta (p. ej. `mi-proyecto-largo-a3`). Son poco legibles, cambian en cada sesión y son ambiguos cuando hay varios agentes en la misma carpeta.
- En la práctica, el usuario bautiza a cada agente por su papel en el primer prompt (`lider`, `dev`, `revisor`, `tester`) y les pide hablar entre ellos por ese nombre.
- Identidad técnica disponible (verificada en el spike):
  - Claude: `CLAUDE_CODE_SESSION_ID` en el entorno del servidor MCP.
  - Codex: `_meta.x-codex-turn-metadata.thread_id` en cada `tools/call`. Codex no pasa variables de entorno a sus servidores MCP.
  - Nodo: el nombre del equipo en Windows; `WSL_DISTRO_NAME` en WSL.
  - herdr: `HERDR_PANE_ID` solo llega al shim de Claude en Windows. Para Codex se cruza `thread_id` con `herdr pane list` (`agent_session.value`).

## Decisión

**Nombre explícito, con un nombre automático de respaldo.**

1. **Cómo se asigna el nombre**, por orden de prioridad:
   - Lo pasa el lanzador: `shepherd launch claude|codex --name <nombre>`. En Claude llega por variable de entorno al shim. En Codex, por `-c mcp_servers.shepherd.env.SHEPHERD_NAME=<nombre>`, porque el `env` declarado en su configuración sí llega al servidor MCP.
   - Lo pide el agente con la herramienta `register(name)`, siguiendo el primer prompt del usuario.
   - Si no hay ninguno de los dos: nombre automático `<carpeta>-<tipo>[-n]` (p. ej. `api-server-codex`), marcado como automático en `list_agents`.
2. **Formato**: minúsculas, dígitos y guiones, de 2 a 32 caracteres. Se direcciona con `nombre` dentro del mismo nodo y con `nombre@nodo` desde otro. Nodos: `pc-<equipo>` y `wsl-<distro>` (p. ej. `revisor@wsl-ubuntu`). Los nombres son únicos por nodo.
3. **El nombre pertenece al papel, no a la sesión.** El broker guarda la relación nombre → identidad técnica actual (nodo, tipo, sessionId o thread_id, carpeta, panel de herdr). Si una sesión nueva se registra con un nombre libre, hereda el buzón y los mensajes pendientes.
4. **Sin robos de nombre:** si el nombre lo tiene una sesión viva, `register` falla. Una sesión cuenta como viva mientras su shim mantenga la conexión con el broker. Al cortarse, el nombre queda libre, pero conserva su buzón.
5. **El remitente nunca es un argumento de herramienta.** El shim lo calcula a partir de su propia sesión y su conexión con el broker. Un agente no puede firmar como otro aunque alguien se lo pida en un prompt.
6. **`list_agents`** devuelve: nombre, nodo, tipo, estado (vivo, parado si se sabe, desconectado), carpeta, panel de herdr si lo hay, si tiene canal (Claude) y si se le puede despertar.

## Alternativas descartadas

- **Solo nombres automáticos:** se repite el problema actual de nombres ilegibles e inestables.
- **Etiqueta del panel de herdr como nombre:** solo funciona dentro de herdr. Se muestra como dato en `list_agents`, pero no es la dirección.

## Consecuencias

- El shim de Codex no conoce su `thread_id` hasta la primera llamada a herramienta. Hasta entonces el broker lo registra con el nombre y el proceso, sin hilo, y no se le puede despertar. Encaja con ADR-0002: un Codex recién arrancado necesita un primer mensaje.
- El broker necesita persistir la tabla de nombres y buzones (ADR del broker / modelo de mensaje).
- Los nombres son visibles para todos los agentes del bus. No deben contener información sensible.
