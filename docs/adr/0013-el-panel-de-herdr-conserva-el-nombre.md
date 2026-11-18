# ADR-0013: Una sesión nueva en un panel de herdr hereda su nombre

- Estado: **Aceptada**
- Fecha: 2026-11-16
- Amplía: los puntos 1 y 2 de [ADR-0008](0008-el-nombre-sigue-a-la-sesion.md)
- Relacionadas: [ADR-0007](0007-lanzador-setup-y-convivencia-herdr.md)

## Contexto

- Con ADR-0008, una sesión que vuelve recupera su nombre, y un nombre automático ajeno no se reutiliza.
- Al desplegarlo (2026-11-16 13:09) se vio el caso contrario: el panel `w7:p1` arrancó una sesión nueva de Claude, con otro identificador. Recibió `mi-proyecto-clau-5`, y el nombre anterior del panel quedó desconectado con su buzón.
- Dentro de herdr el panel representa el papel: el usuario abre una sesión nueva en el mismo sitio para seguir el mismo trabajo, y los demás agentes le siguen escribiendo al nombre de siempre.

## Decisión

**Una sesión sin nombre y sin fila propia hereda el nombre del último ocupante de su panel de herdr, si ya no está conectado.**

1. **Orden al registrarse sin nombre:**
   1. La fila de la misma sesión (ADR-0008).
   2. La fila del panel: mismo nodo, mismo tipo de agente, mismo panel de herdr y misma carpeta, desconectada. Si hay varias, la vista más recientemente.
   3. Un nombre automático nuevo.
2. **Qué se hereda.** El nombre, automático o elegido con `register`, y con él el buzón y los mensajes pendientes.
3. **Por qué también la carpeta.** Los identificadores de panel se pueden repetir en otro espacio de trabajo de herdr. Exigir la misma carpeta evita heredar el nombre de un proyecto ajeno.
4. **Ocupante conectado.** Si el ocupante anterior sigue conectado, no se hereda nada: la sesión nueva recibe un nombre automático nuevo.

## Alternativas descartadas

- **Heredar sin comprobar la carpeta:** un panel reutilizado para otro proyecto recibiría el correo del anterior.
- **Pedir al usuario que use `register` o `launch --name`:** es lo que se quería evitar con los nombres automáticos.

## Consecuencias

- La sesión anterior del panel, si vuelve (p. ej. con `--resume` en otro panel), ya no tiene fila propia y recibe un nombre automático nuevo.
- Solo aplica a Claude en Windows, el único caso en que el shim conoce su panel de herdr (ADR-0003).
- Las sesiones que ya tienen fila propia la conservan. Un nombre como `…-clau-5` no vuelve solo al nombre anterior del panel: hay que usar `register`.
