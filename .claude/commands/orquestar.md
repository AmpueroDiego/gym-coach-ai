---
description: Orquestador de gym-coach con modos de manejo (eco | sport | blindado | auto) — planner → coders → qa + adversary.
argument-hint: "[eco|sport|blindado|auto] <tarea>"
---

Actúas como orquestador de gym-coach. La tarea es: $ARGUMENTS

## Modo de manejo

Si la primera palabra de la tarea es `eco`, `sport`, `blindado` o `auto`, ese es el modo y el resto es la tarea. Si no hay modo, es `sport` (Diego quiere Enjambre siempre; solo baja a `eco` o sube a `blindado` si él lo pide). Anuncia a Diego en una línea el modo elegido (y en `auto`, por qué).

| Modo | Para qué | Cómo corre |
|---|---|---|
| `eco` | gastar pocos tokens | Sin paralelo: 1 `coder` (Sonnet), 1 `qa`. Planner en Sonnet, o ninguno si el cambio es obvio. `adversary` (1, diff completo) solo si toca auth, datos, coach o Azure. 1 vuelta de arreglos, solo lo grave. |
| `sport` | máxima velocidad (Modo Enjambre) | Todas las reglas de velocidad y el Modo Enjambre de abajo: hasta 6 coders en paralelo, revisión en pipeline por bloque, revisores en Sonnet, solo lo grave abre otra vuelta. |
| `blindado` | máxima calidad y seguridad | Todos los agentes con `model: "opus"`. `adversary` siempre, en sus 4 frentes. Coders en paralelo por bloque (para no perder tiempo), pero todo hallazgo real (también menores) entra a arreglos; hasta 2 vueltas, y la re-revisión incluye al `adversary` de los frentes tocados. |
| `auto` | que el orquestador elija | Solo un cambio trivial de 1 archivo → `eco`. Todo lo demás → `sport`. Si toca auth, aislamiento de datos, el coach o crea recursos de Azure → esos bloques y su revisión en `blindado`, el resto en `sport`. |

Todo lo que sigue describe el modo `sport`; `eco` y `blindado` lo ajustan según la tabla. Lo que no cambia en ningún modo: rama, plan con OK de Diego (si hay planner), validación en backend, nada de recursos de Azure sin OK, commit al final.

## Reglas de velocidad (modo `sport`)

Reglas de velocidad y costo (Diego prefiere que tarde menos aunque cueste algo más de tokens):
- **Todo lo que no depende entre sí corre en paralelo**: varios agentes del mismo tipo lanzados en un mismo mensaje.
- **Cada agente recibe solo su parte**: la lista de archivos o carpetas que le tocan, el contrato que necesita y nada más. No lo dejes explorar el repo entero.
- **Re-revisar solo lo que cambió** en esa vuelta, y solo con los agentes de esos bloques o frentes.
- Modelos: `planner` en Opus; `coder` en Sonnet, salvo los bloques que tocan auth, aislamiento de datos o el coach, que se lanzan con `model: "opus"`; `qa` y `adversary` en Sonnet con esfuerzo medio (fijado en sus definiciones).
- **Pipeline, no barrera**: en cuanto termina el coder de un bloque, lanza ya su `qa` (y el `adversary` de su frente). No esperes a que terminen todos los coders.
- **Solo lo grave abre otra vuelta**: bloqueante, crítico, alto o medio. Los menores y bajos van a la lista de pendientes del reporte final, no a otra vuelta de arreglos.
- El planner escribe un plan tan concreto (archivos, firmas, contrato, casos de prueba) que el coder no tenga que explorar ni decidir.

**Más Enjambre (regla de Diego, siempre)**: los arreglos que salgan de la revisión TAMBIÉN se reparten en paralelo por archivo (nunca un solo coder en serie con una lista larga); revisores partidos por zona de archivos; el orquestador no espera: en cuanto un bloque cierra, lanza su revisión.

**Modo Enjambre (`sport` y `auto`)** — atajos para trabajos medianos y grandes (meta: ~85% menos tiempo en cambios medianos, ~75% en etapas nuevas):
1. **Arreglos chicos los hace el orquestador**: si un hallazgo se arregla con menos de ~20 líneas en 1–2 archivos, edítalo tú directamente y córrele sus pruebas; no lances un coder.
2. **Reusar agentes**: para arreglos y re-revisiones, escríbele con `SendMessage` al mismo coder/qa que ya conoce ese bloque, en vez de lanzar uno nuevo que tenga que releer todo.
3. **Planner según tamaño**: etapas nuevas o diseño de datos/auth → planner en Opus; cambios medianos sobre código existente → planner con `model: "sonnet"`.
4. **Sin planner en lo obvio**: si el cambio es de 1–2 archivos y está claro, salta el planner; dale al coder el pedido directo con los archivos y los casos de prueba.
5. **Una sola corrida completa de pruebas**: cada agente corre solo las pruebas de su bloque; la suite completa (build + tests + lint) la corre el orquestador una vez, antes del commit.

1. **Rama**: desde `develop` actualizado, crea `feature/<slug>` o `fix/<slug>`.
2. **Plan**: lanza `planner`. Debe:
   - fijar primero el **contrato** (rutas, payloads, errores, tipos compartidos) para que los bloques no dependan entre sí;
   - repartir el trabajo en **2 a 6 bloques con archivos disjuntos** (prefiere más bloques y más chicos antes que pocos grandes) (p. ej. backend-datos, backend-auth/limites, frontend-estado, frontend-pantallas). Cada archivo pertenece a un solo bloque; los de la raíz (`package.json`, `README.md`, `swa-cli.config.json`, `.sln`) van a un solo bloque;
   - marcar qué bloque, si alguno, debe ir antes que otro (scaffold inicial).
   Muéstrale a Diego el plan resumido y espera su OK. Si el plan trae dos opciones, que Diego elija.
3. **Código**: si hay un bloque de scaffold, córrelo primero con un `coder`. Después lanza **un `coder` por bloque, todos en paralelo** (máximo 6). Cada coder toca solo los archivos de su bloque y corre solo sus pruebas. Al terminar todos, corre una vez build + pruebas completas para detectar choques.
4. **Revisión**, todo en paralelo en un mismo mensaje:
   - `qa`: uno por bloque, con la lista de archivos de ese bloque.
   - `adversary`: si el cambio toca datos, auth, el coach o recursos de Azure, uno por frente afectado (máximo 4), con sus archivos:
     1. **auth**: tokens, firmas, headers, rutas sin validar.
     2. **datos**: aislamiento por `userId`, particiones, inyección, qué se filtra en las respuestas.
     3. **costos y abuso**: RU, limitador, almacenamiento, recursos de Azure.
     4. **frontend y offline**: estado local, `localStorage`, cambio de cuenta, reintentos.
5. **Arreglos**: junta los hallazgos graves (ver regla de arriba) en una sola lista, repártelos por bloque y lanza los coders de esos bloques en paralelo. La re-revisión la hace un solo `qa` por bloque, enfocado en esos arreglos; el `adversary` solo vuelve si el arreglo tocó auth o aislamiento de datos. Normalmente basta 1 vuelta; máximo 2. Si sigue fallando, para y reporta.
6. **Cierre**: commit en conventional commits en español. Push y PR a `develop` solo si Diego lo pide (a producción se pasa con un PR de `develop` a `main`).

Reporte final a Diego, corto: qué se hizo, qué se probó, qué quedó pendiente y cualquier recurso de Azure que haya que crear (con su comando y costo).
