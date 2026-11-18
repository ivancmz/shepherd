# ADR-0012: Despertar a Claude con el hook `Stop`

- Estado: **Aceptada**
- Fecha: 2026-11-16
- Amplía: [ADR-0002](0002-entrega-y-despertar.md) (vías de aviso) y [ADR-0007](0007-lanzador-setup-y-convivencia-herdr.md) (`setup`)
- Relacionadas: [ADR-0009](0009-reintentos-y-recordatorios.md)

## Contexto

- En uso real (14–16 nov 2026) los 75 avisos a Claude salieron por `herdr agent prompt`. El canal de Claude no se usó ninguna vez: exige un flag de desarrollo y una confirmación en cada arranque.
- `herdr agent prompt` solo sirve dentro de herdr, escribe en el terminal como si fuera el usuario y falla si hay un diálogo abierto.
- Fuera de herdr y sin canal, un Claude no se entera de que tiene correo.
- Claude Code permite hooks de comando con `asyncRewake`: corren en segundo plano y, si terminan con código 2, despiertan a Claude y le muestran su salida de error como recordatorio del sistema.
- Prueba del 2026-11-16 (Claude Code 2.1.325, Windows, sesión interactiva inactiva):
  - Un hook `Stop` con `asyncRewake` que termina con código 2 despertó a la sesión dos veces seguidas, 1 s después de terminar, sin entrada de teclado.
  - Cada despertar produce un turno y, al acabar, `Stop` vuelve a dispararse: el hook se rearma solo.
  - El texto llega bajo el título "Stop hook blocking error".
  - El hook recibe el identificador de sesión por la entrada estándar.

## Decisión

**Una sesión de Claude parada espera su correo con `shepherd wait`, lanzado por el hook `Stop`.**

1. **`shepherd wait`.** Lee el identificador de sesión de la entrada del hook, se conecta al broker y espera. Cuando el broker tiene correo que anunciar a esa sesión, termina con código 2 y el aviso fijo en la salida de error. En cualquier otro caso (sesión desconocida, broker antiguo o caído mucho tiempo, otra espera más nueva) termina con código 0 y sin texto.
2. **Broker.** Guarda como mucho una espera por agente; una nueva sustituye a la anterior. Al anunciar correo, la espera tiene prioridad sobre `herdr agent prompt` y `codex queue`. Si no hay espera, se usan las vías de ADR-0002 como hasta ahora.
3. **Correo llegado durante un turno.** Si al registrarse la espera hay mensajes en `queued`, se anuncian en ese momento.
4. **Texto.** El mismo aviso fijo de ADR-0002 y el mismo recordatorio de ADR-0009, más una frase que aclara que es un aviso y no un error. Nunca lleva contenido del mensaje.
5. **`setup`.** Añade a `~/.claude/settings.json` un hook `Stop` con `asyncRewake` que ejecuta `shepherd wait`, con copia de seguridad previa. `setup --undo` lo quita. Si el fichero no se puede interpretar, `setup` lo deja intacto y avisa.
6. **El canal de Claude se mantiene.** Se decidirá si se retira tras un periodo de uso real del hook.

## Alternativas descartadas

- **Retirar ya el canal:** el canal entrega el mensaje completo y lo marca como leído; el hook solo entrega un aviso. Falta uso real para comparar.
- **Hook `Stop` síncrono que bloquea si hay correo:** solo actúa al terminar un turno; no despierta a una sesión inactiva.
- **Sondeo desde el hook:** la espera conectada al broker no consume nada mientras no hay correo y reacciona al instante.

## Consecuencias

- Claude recibe avisos fuera de herdr y sin teclear en su terminal.
- Hay un proceso `shepherd wait` por sesión de Claude parada.
- Una sesión recién abierta no tiene espera hasta que termina su primer turno. Hasta entonces se usan las vías anteriores.
- Si el broker se reinicia, la espera se reconecta durante unos minutos; si no lo consigue, termina y la sesión queda sin espera hasta su siguiente turno.
- El aviso por la espera se da por entregado al responder el broker. Si el proceso muere justo entonces, el mensaje queda como avisado y lo cubren los recordatorios de ADR-0009.
- Al reescribir `settings.json`, `setup` puede cambiar su sangrado.
- Sin probar todavía: qué ve el usuario si el aviso llega mientras escribe.
