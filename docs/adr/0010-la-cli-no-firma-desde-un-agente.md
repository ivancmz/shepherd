# ADR-0010: La CLI no envía ni lee como el usuario desde un agente

- Estado: **Aceptada**
- Fecha: 2026-11-16
- Matiza: el punto 5 de [ADR-0003](0003-identidad-y-nombres.md) y "El usuario como participante" de [ADR-0006](0006-modelo-de-mensaje.md)

## Contexto

- `shepherd send` y `shepherd reply` envían marcados como humano. `shepherd inbox` lee el buzón del usuario y lo marca como leído.
- La CLI no comprobaba quién la ejecuta. Cualquier agente tiene shell, así que podía enviar con la autoridad del usuario o vaciar su buzón.
- Todos los agentes corren con la misma cuenta del sistema que el usuario. Un proceso decidido a suplantarlo puede conectarse al broker directamente: no hay frontera de seguridad posible aquí.

## Decisión

**`send`, `reply` e `inbox` se niegan a ejecutarse dentro de un agente.**

1. La CLI se considera dentro de un agente si existe la variable `CLAUDECODE`, que Claude Code pone en los comandos que lanza, o si alguno de sus procesos antecesores es `claude` o `codex`. Lo segundo cubre a Codex, cuyo entorno no está documentado.
2. En ese caso falla con un mensaje que remite a las herramientas MCP de shepherd.
3. `agents`, `status` y el resto de órdenes no cambian.
4. No hay variable para saltarse la comprobación.
5. La documentación describe la marca de humano como **mejor esfuerzo**, no como garantía.

## Alternativas descartadas

- **Enviar sin marca de humano:** crea un remitente nuevo sin buzón claro y más casos que probar.
- **Solo corregir la documentación:** deja abierto el caso probable, un agente que usa la CLI por error o por un prompt inyectado.

## Consecuencias

- Un script del usuario lanzado desde un agente no puede enviar como el usuario. Tampoco la orden `! shepherd send` escrita en el prompt de Claude Code.
- Un agente malicioso puede evitar la comprobación limpiando el entorno y desligándose del proceso padre. Se asume.
