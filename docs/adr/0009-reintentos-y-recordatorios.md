# ADR-0009: Reintentos sin límite y recordatorios de correo sin leer

- Estado: **Aceptada**
- Fecha: 2026-11-16
- Amplía: [ADR-0002](0002-entrega-y-despertar.md) (reintentos del aviso)
- Relacionadas: [ADR-0006](0006-modelo-de-mensaje.md)

## Contexto

- El aviso a un agente se reintentaba a los 2, 10 y 30 s y después se abandonaba. Caso observado: una petición de las 14:02 se entregó a las 14:59 porque el agente estaba bloqueado en un diálogo cuando llegaron los cuatro intentos.
- Un mensaje pasa a `notified` cuando el aviso se entrega al transporte. Si el agente no lo lee, nadie vuelve a avisar: solo se anunciaban los mensajes en `queued`.
- Al registrarse, el broker devolvía un aviso de correo sin leer que el shim descartaba.

## Decisión

**El broker revisa cada minuto a los destinatarios conectados con correo sin leer.**

1. **Aviso fallido.** Tras los intentos a los 2, 10 y 30 s, se reintenta cada **2 minutos** mientras el mensaje siga en `queued` y el destinatario esté conectado. Sin límite.
2. **Avisado pero sin leer.** Un recordatorio a los **10 minutos** del último aviso y otro a la **hora**. Después se para. Un mensaje nuevo reinicia la cuenta.
3. **Al volver a registrarse.** Un único recordatorio si quedan mensajes en `notified`, salvo que se haya enviado otro hace menos de 10 minutos.
4. **Texto.** El recordatorio es un texto fijo con el número de mensajes y los remitentes, igual que el aviso (ADR-0002): nunca lleva contenido del mensaje.
5. **Sin vía de aviso.** El shim añade el aviso de correo pendiente del registro al resultado de la siguiente herramienta, para las sesiones sin canal.
6. Los tiempos son configurables en el broker.

## Alternativas descartadas

- **Hook `Stop` de Claude que avise del correo sin leer:** no despierta a una sesión inactiva y solo sirve para Claude. Se valorará con el resultado de la prueba de `asyncRewake`.
- **Recordatorios sin fin:** un agente que decide no leer recibiría texto en su terminal para siempre.

## Consecuencias

- La cuenta de recordatorios vive en memoria. Tras reiniciar el broker, el correo antiguo sin leer recibe un recordatorio más, no dos.
- Un agente bloqueado en un diálogo recibe el aviso como mucho 2 minutos después de quedar libre.
- Los destinatarios desconectados no reciben reintentos ni recordatorios: se les avisa al volver.
