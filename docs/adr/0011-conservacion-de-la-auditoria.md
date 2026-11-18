# ADR-0011: Conservación de la auditoría

- Estado: **Aceptada**
- Fecha: 2026-11-16
- Sustituye: el apartado "Conservación" de [ADR-0006](0006-modelo-de-mensaje.md)
- Relacionadas: [ADR-0004](0004-broker-local.md)

## Contexto

- Los mensajes leídos se borran de la base de datos a los 90 días, pero su texto completo queda en `audit-YYYY-MM.jsonl` y nadie lo borra.
- La limpieza de la base de datos solo se ejecutaba al arrancar el broker. Con el broker encendido durante semanas, tampoco ahí se cumplían los 90 días.
- El fichero de auditoría empezaba con una marca BOM, que rompe los lectores de JSONL.

## Decisión

1. **Un solo plazo.** Los mensajes leídos o respondidos se borran a los **90 días** (configurable). Los no leídos no caducan.
2. **Auditoría.** Cada fichero mensual se borra entero cuando todo su mes supera ese plazo. Sigue guardando el contenido completo (ADR-0004).
3. **Cuándo.** La limpieza de mensajes, auditoría y agentes (ADR-0008) se ejecuta al arrancar el broker y una vez al día.
4. **Formato.** La auditoría se escribe en UTF-8 sin BOM.

## Alternativas descartadas

- **Auditoría solo con metadatos:** se pierde el texto para diagnosticar un fallo de contenido cuando el mensaje ya se leyó.
- **Dejarlo y documentarlo:** el disco crece sin límite.

## Consecuencias

- Al borrar el fichero mensual entero, un mensaje puede vivir en la auditoría hasta unos 120 días.
- Los ficheros ya existentes conservan su BOM inicial; los nuevos no lo llevan.
