# ADR-0008: El nombre sigue a la sesión

- Estado: **Aceptada**. Los puntos 1 y 2 están ampliados por [ADR-0013](0013-el-panel-de-herdr-conserva-el-nombre.md)
- Fecha: 2026-11-16
- Sustituye: los puntos 1 (nombre automático) y 3 de [ADR-0003](0003-identidad-y-nombres.md)
- Relacionadas: [ADR-0002](0002-entrega-y-despertar.md), [ADR-0005](0005-protocolo-de-nodo.md)

## Contexto

- En uso real (14–16 nov 2026) casi todos los agentes usan nombre automático `<carpeta>-<tipo>[-n]`.
- El sufijo `-n` se calculaba mirando solo qué nombres estaban vivos en ese momento. Al reiniciar el broker o las sesiones, los nombres se repartían de nuevo por orden de llegada.
- Caso observado: la sesión `02bfe027` fue `…-clau-3` los días 14 y 15 y `…-claude` el 16. Los tres mensajes sin leer de `…-clau-4` pasaron a ser de otra sesión.
- Un nombre elegido con `register` solo vivía en la memoria del shim: se perdía al reiniciar la sesión.
- La fila de un agente conservaba el `thread_id` de Codex de la sesión anterior aunque cambiara de sesión, por un `COALESCE` en la escritura.
- La tabla de agentes no se limpiaba nunca.

## Decisión

**Si la sesión no pide nombre, el broker le devuelve el que ya tenía.**

1. **Registro sin nombre.** El broker busca en su nodo la fila con la misma clave de sesión. Si existe, la sesión recupera ese nombre, sea automático o elegido con `register`.
2. **Nombre automático nuevo.** Si no hay fila, se asigna `<carpeta>-<tipo>[-n]`. Un nombre que ya tiene fila de otra sesión no se reutiliza, aunque esa sesión esté desconectada: se pasa al siguiente sufijo.
3. **Nombres explícitos.** Sin cambios respecto a ADR-0003: `shepherd launch --name` y `register(name)` pertenecen al papel. Una sesión nueva que pide un nombre explícito libre hereda su buzón. El punto 4 (sin robos de nombre) sigue igual.
4. **Cambio de nombre.** Cuando una sesión se registra con otro nombre, la fila anterior deja de estar asociada a esa sesión. Si era automática y nunca envió ni recibió mensajes, se borra y su nombre queda libre.
5. **Codex.** Su clave de sesión es el proceso, que cambia en cada arranque. Al llegar el `thread_id` en la primera llamada a herramienta, si la sesión tiene nombre automático y existe una fila desconectada de Codex con ese `thread_id` en el mismo nodo, la sesión adopta ese nombre. El correo recibido con el nombre provisional pasa al nombre recuperado y la fila provisional se borra. `identify` devuelve el nombre resultante para que el shim lo actualice.
6. **`thread_id`.** Solo se conserva si la fila sigue siendo de la misma sesión. Un `thread_id` pertenece a una sola fila.
7. **Limpieza.** Una vez al día se borran las filas con nombre automático que llevan **14 días** desconectadas (configurable) y no tienen correo sin leer.

## Alternativas descartadas

- **Guardar el nombre en el shim o en un fichero por sesión:** no sobrevive a un cambio de proceso y duplica un dato que el broker ya tiene.
- **Nombres automáticos con un identificador aleatorio:** estables, pero ilegibles, que es lo que ADR-0003 quería evitar.

## Consecuencias

- Los mensajes sin leer de un nombre automático ya no pueden acabar en otra sesión.
- Con sesiones que van y vienen, los sufijos crecen (`-5`, `-6`…) hasta que la limpieza libera los antiguos. Es el precio de no reutilizar nombres ajenos.
- Un Codex nuevo tiene un nombre provisional hasta su primera llamada a herramienta. Ya ocurría con el despertar (ADR-0003, consecuencias).
- Responder a un mensaje de un agente automático borrado por la limpieza falla con "agente desconocido".
- La respuesta de `identify` gana dos campos opcionales (`name`, `address`). Un shim antiguo los ignora.
