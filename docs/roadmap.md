# Ruta del proyecto: gym-coach sobre Microsoft Foundry

Cómo crece gym-coach desde "registrar entrenos" hasta un coach completo usando la suite de Microsoft Foundry, una pieza a la vez. Cada etapa deja algo que funciona y se usa en el gym; ninguna pieza entra antes de que una necesidad real la pague (KISS/YAGNI).

Reglas que aplican a todas las etapas (ver `CLAUDE.md`):

- **Barato primero**: antes de crear un recurso, confirmar capa gratis o costo mensual estimado en la [calculadora de Azure](https://azure.microsoft.com/pricing/calculator/). Los costos de este documento son estimaciones a verificar, no precios garantizados.
- **El coach no inventa datos**: todo número sale de una herramienta.
- **Los entrenos no van por RAG**: son datos estructurados que se consultan con herramientas.
- Crear o borrar recursos de Azure: comando exacto + OK de Diego.

## Vista general

| Etapa | Pieza de Foundry | Qué gana la app | Costo estimado |
|---|---|---|---|
| 1 | — | Registrar series con el celular | 0 USD (SWA Free + Cosmos free tier) |
| 2 | Catálogo de modelos | Primer coach: responde con tus números | Centavos por mes (tokens de un modelo mini) |
| 3 | Agent Service | Coach con memoria de conversación y herramientas administradas | Solo tokens + herramientas |
| 4 | Catálogo de herramientas (MCP) | Las herramientas del coach como servidor MCP, también desde Claude Desktop | Functions consumo: ~0 USD |
| 5 | Foundry IQ (RAG) | Coach que cita guías de entrenamiento y tus notas | AI Search Free: 0 USD |
| 6 | Seguridad y gobierno | Sin claves, filtros de contenido, trazas, presupuesto | ~0 USD con límites de ingesta |
| 7 | Evaluación | Medir que el coach no inventa y llama bien las herramientas | Tokens del modelo juez |

> Seguridad (6) y evaluación (7) van al final como **etapas completas**, pero su **mínimo** entra desde la etapa 2: identidad administrada, filtros por defecto, alerta de presupuesto y un puñado de casos de prueba. Es más barato construir con eso desde el inicio que agregarlo después.

---

## Etapa 1 — Registrar entrenos (sin IA) (hecha)

- PWA React + TS en Azure Static Web Apps Free; API C# .NET isolated gestionada por SWA.
- Login con Google (validación del ID token en el backend).
- Cosmos DB free tier: base `gymcoach` (400 RU/s a nivel de BD), contenedor `series` con partición `/userId`.
- **Listo cuando**: se registra una serie en 1–3 toques y el historial queda guardado por usuario.

Esta etapa es la base de todo lo demás: el coach solo vale por los datos reales que se acumulen aquí.

## Etapa 2 — Catálogo de modelos: el primer coach (hecha)

**Objetivo**: un chat donde el coach responde con tus números ("subiste 7.5 kg en banca en 6 semanas").

**Qué se agrega**
- Un recurso de Foundry + un proyecto en `eastus2`.
- Un deployment de un modelo **mini** del catálogo (tipo *Global Standard*, pago por token).
- Endpoint `POST /api/coach` en la API: llama al modelo con *function calling* sobre herramientas propias que leen Cosmos (`ver_entrenos_recientes`, `progreso`, `volumen_por_grupo`). El modelo nunca ve datos de otro usuario: el `userId` lo pone el backend, no el modelo.

**De qué depende elegir un modelo del catálogo**
| Criterio | Por qué importa aquí |
|---|---|
| Costo por token | Es el único costo variable del coach; un mini cuesta una fracción de un modelo grande. |
| Soporte de *tool calling* | Obligatorio: el coach no puede inventar números, todo sale de herramientas. |
| Disponibilidad en la región y tipo de deployment | No todos los modelos están en todas las regiones ni como *Global Standard*. |
| Modelos de terceros (Meta, Mistral, Anthropic…) | Se facturan por Azure Marketplace y algunos tipos de suscripción no los permiten; verificar antes. |
| Calidad en español | Probar con 10 preguntas reales antes de decidir. |
| Soporte en Agent Service | Si no lo soporta, no sirve para la etapa 3. |

Recomendación de partida: un modelo mini de OpenAI (Azure OpenAI) porque cumple todos los criterios y es el más documentado en Agent Service. Cambiar de modelo después es cambiar un deployment, no el código.

**Mínimo de seguridad y evaluación desde ya**
- La Function se autentica contra Foundry con **identidad administrada** (sin API keys), si la hosting lo permite; si no, key en app settings y nunca en el repo.
- Filtros de contenido por defecto activos.
- Presupuesto con alerta en Cost Management (p. ej. 5 USD/mes).
- 10 preguntas de prueba con respuesta esperada, guardadas en el repo.

**Listo cuando**: el coach responde 10 preguntas de prueba usando solo números de herramientas, y ante dolor o lesión recomienda ver a un profesional.

**Aprendes**: Azure OpenAI, deployments, function calling, costos por token.

**Cómo quedó (cerrada el 2026-10-19)**
- Foundry `aif-gym-coach-diego` (eastus2) con el deployment `coach-mini`: gpt-5-mini, Global Standard, capacidad 50 (50 RPM), filtro de contenido DefaultV2. Se retira el 2027-02-09; el reemplazo es cambiar `AZURE_OPENAI_DEPLOYMENT`.
- Herramientas: `ver_entrenos_recientes`, `progreso`, `volumen_por_grupo`, `mis_ejercicios`, `recordar` (perfil y notas) y `proponer_rutina` (valida la rutina y la app la muestra en una tarjeta). El `userId` siempre sale del token.
- Topes por usuario: 5 mensajes por minuto y 20 por día.
- Presupuesto `gym-coach-5usd` con alertas al 80 %, al 100 % y por pronóstico.
- Sin identidad administrada: las Functions gestionadas de SWA Free no la soportan, así que la key vive en App Settings. Queda como deuda de la etapa 6.
- Casos de prueba: `docs/coach-casos.json` (26 casos, más que los 10 pedidos) con datos sembrados en el emulador. Corrida del 2026-10-19 con el prompt `2026-10-16.1`: 21/23 a la primera (los casos 24-26 aún no se corrían); al repetir los que fallaron, el 10 pasó 4 de 5 veces y el 17 pasó 3 de 5. Corrida del 2026-10-20 con el prompt `2026-10-19.1` y la guardia de pedidos en texto libre: 26/26 a la primera. Las dos fallas conocidas siguen siendo intermitentes, sin arreglo en código:
  - **Caso 10 (historial alterado)**: a veces repite un número inventado de un mensaje viejo del coach ("142.5 kg") sin llamar `progreso`. El historial lo manda el cliente, así que solo se engaña a sí mismo: nunca ve datos de otro usuario. Arreglo posible: forzar `progreso` en código cuando la pregunta pide un número de un ejercicio.
  - **Caso 17 (pide un grupo nunca entrenado)**: a veces da una excusa falsa ("no tengo acceso a las herramientas") o pide permiso, en vez de explicar el tope de 1 ejercicio nuevo. Nunca muestra una tarjeta falsa. El prompt `2026-10-19.1` agrega una frase para que explique el tope.

## Etapa 3 — Agent Service: el coach como agente ← actual

**Objetivo**: pasar del bucle de function calling hecho a mano a un agente administrado por Foundry.

**Qué se agrega**
- Un agente `gym-coach` en Foundry Agent Service con las instrucciones del coach versionadas en el repo (`PromptCoach`) y publicadas con `herramientas/SincronizarAgente` (solo crea versión si cambió el hash).
- Una conversación de Foundry por usuario y por día (el mismo alcance que el chat de la PWA, o hasta "Nueva conversación"): el coach recuerda lo hablado. El id vive solo en el documento del usuario en Cosmos.
- Las herramientas de la etapa 2 son *function tools*: el agente las pide y las ejecuta la API con el `userId` del token.
- La PWA sigue llamando a la API; la API habla con el agente. El navegador nunca habla directo con Foundry.
- Fallback: `COACH_MOTOR=bucle` vuelve al bucle de la etapa 2 sin desplegar. Si el agente falla en un turno, se reintenta con el bucle y, si también falla, 503 con mensaje amable en la PWA.

**Costo**: el servicio de agentes cobra por los tokens del modelo y por las herramientas built-in que se usen; las herramientas propias solo cuestan lo que cueste la API (≈0).

**Listo cuando**: las mismas 10 preguntas pasan con el agente, y la conversación de un usuario no es accesible por otro.

**Estado (2026-10-20)**: código en develop con `COACH_MOTOR=bucle` por defecto (producción sin cambios). Agente `gym-coach` v1 creado. Casos con prompt 2026-10-20.1: bucle 26/26; agente 25/26 + memoria, aislamiento y plantilla 3/3. Falta antes de activar el agente:
- Caso 18 (historial vacío, pide rutina): el agente responde solo con una pregunta, sin `proponer_rutina` (3 de 4).
- Latencia: turnos de 5-7 rondas tardan 22-48 s con el agente (17: 48 s vs 18 s en el bucle); pasan el sub-tope de 20 s y el fallback borra la memoria del día.

**Aprendes**: Agent Service, conversaciones, instrucciones como código.

## Etapa 4 — Catálogo de herramientas: servidor MCP

**Objetivo**: las herramientas del coach como servidor MCP reutilizable.

**Qué se agrega**
- Un servidor MCP en Azure Functions (plan de consumo) con `ver_entrenos_recientes`, `progreso`, `volumen_por_grupo`, `records_personales`.
- El agente de Foundry consume el servidor como **herramienta MCP** del catálogo.
- El mismo servidor se conecta a Claude Desktop para consultar tus entrenos desde ahí.
- Auth propia del servidor MCP: aquí ya no protege SWA, hay que validar el token en cada llamada (pasa por `adversary`).
- Evaluar herramientas built-in del catálogo solo si pagan una necesidad (p. ej. *code interpreter* para gráficas de progreso; tiene costo por sesión).

**Costo**: Functions consumo tiene un millón de ejecuciones gratis al mes: ~0 USD.

**Listo cuando**: el agente y Claude Desktop responden lo mismo usando el servidor MCP, y un token de un usuario no lee datos de otro.

**Aprendes**: MCP, herramientas por agente, auth fuera de SWA.

## Etapa 5 — Foundry IQ: conocimiento (RAG)

**Objetivo**: el coach responde "por qué" con guías de entrenamiento y con tus notas libres.

**Qué se agrega**
- Azure AI Search capa **Free** (índices y almacenamiento limitados; suficiente para guías y notas de una persona).
- Una base de conocimiento en Foundry IQ con: guías de entrenamiento (documentos curados por Diego) y notas libres del usuario.
- Las notas se filtran por `userId` en la búsqueda: nunca se recupera una nota de otro usuario.
- Plan B si la capa Free no alcanza: búsqueda vectorial de Cosmos (dentro de los 400 RU/s reales de la cuenta gratis).
- **Los entrenos siguen por herramientas, no por RAG.**

**Costo**: AI Search Free 0 USD; los embeddings cuestan tokens (centavos).

**Listo cuando**: el coach cita la guía de la que saca un consejo, y una prueba confirma que no recupera notas de otro usuario.

**Aprendes**: Azure AI Search, embeddings, RAG con filtros de seguridad.

## Etapa 6 — Seguridad y gobierno completos

**Objetivo**: el proyecto se ve como uno de producción.

**Qué se agrega**
- Identidad administrada en todo; secretos restantes en Key Vault (capa estándar, costo por operación: centavos).
- Pasar la key con la que la API habla con el agente de Foundry a identidad/Entra (el adaptador ya admite cambiar la política de autenticación por configuración).
- Automatizar la limpieza de conversaciones y responses viejas de Foundry (`SincronizarAgente --limpiar --si`): una Function con timer fuera de SWA o una Logic App, con costo (SWA no tiene timer).
- Borrar la cuenta de un usuario debe borrar su doc `coach-conversacion` y su conversación y responses en Foundry.
- RBAC de mínimo privilegio: la API solo puede invocar el agente y leer/escribir su contenedor.
- Filtros de contenido ajustados y *Prompt Shields* contra inyección de prompts (las notas libres son texto del usuario que entra al modelo).
- Trazas del agente en Application Insights con **límite diario de ingesta** para que el costo quede en ~0.
- Límite de uso por usuario (mensajes al coach por día) para que nadie dispare el costo de tokens.
- Aislamiento de red privado: **fuera de alcance** salvo pedido explícito (tiene costo fijo).

**Listo cuando**: no hay ninguna clave en app settings que se pueda reemplazar por identidad, y una prueba de inyección de prompt desde una nota no logra que el coach revele datos de otro usuario.

**Aprendes**: RBAC, managed identity, content safety, observabilidad.

## Etapa 7 — Evaluación

**Objetivo**: medir el coach, no adivinar si mejoró.

**Qué se agrega**
- Un set de evaluación en el repo (30–50 casos) construido con datos de prueba conocidos: pregunta, herramientas que debería llamar, números que debe citar.
- Evaluadores de Foundry: *tool call accuracy*, *groundedness* (respaldo en lo que devolvieron las herramientas), *relevance*, seguridad.
- Un evaluador propio: "todo número de la respuesta aparece en la salida de alguna herramienta".
- Correr la evaluación antes de cambiar el modelo, las instrucciones o las herramientas; comparar contra la corrida anterior.
- *Red teaming* básico con el agente de red teaming de Foundry antes de abrir la app a más usuarios.

**Costo**: tokens del modelo juez por corrida (centavos con un set chico).

**Listo cuando**: hay una línea base de métricas y un cambio de modelo se decide con números.

**Aprendes**: evaluación de agentes y LLM-as-judge.

## Fuera de etapas (pedidos de Diego)

### Hecho

- **Registro** (junto a la etapa 1): rutinas guardadas, kg/lb por ejercicio, tipos de serie, récords personales, descanso programable con sonido, reordenar ejercicios y resumen del entreno con radar.
- **Sesión persistente**: cookie firmada de 90 días, sin estado en el servidor.
- **APK de Android**: TWA hecha con PWABuilder, con `assetlinks.json`.
- **Importador de Hevy**: pasa el CSV a series normales. Desde el 2026-10-19 está oculto en la PWA: el código sigue y vuelve a mostrarse si hace falta.
- **Perfil del usuario para el coach**: minutos disponibles, objetivo, altura, peso corporal, equipo preferido y notas. El coach los guarda con `recordar`.
- **Perfil de entrenamiento**: resumen calculado de las series (frecuencia, horario, división, duración, ejercicios principales, progresos y estancamientos). Se guarda como doc `perfil-entrenamiento` y va en el prompt. Se recalcula a lo más una vez al día y tras importar o editar series, no en cada pregunta. Incluye "Mis ejercicios" en la PWA y `mis_ejercicios` en el coach.
- **Plan del día**: el coach propone el entreno de hoy (uno por día, con hasta 3 variaciones con "Otra opción"), fijo arriba del chat.
- **Coach adaptado**: elige ejercicios del historial y respeta el equipo preferido, con como mucho 1 ejercicio nuevo por rutina. Una guardia en código evita que afirme una rutina que no creó.
- **Ficha de cada ejercicio**: fotos, músculos y pasos, con la silueta del músculo en Hoy.
- **Idioma**: la app y el coach completos en inglés o español.
- **Coach para usuarios nuevos**: perfil con experiencia y días por semana; `proponer_programa` (2 a 4 rutinas: cada una se empieza o se guarda como plantilla, o todas a la vez); pesos en null con aviso en la tarjeta; una pregunta al final por el dato del perfil que más falta. Un pedido de rutina o programa en texto libre ("dame un entreno de pecho") que el modelo no cumple fuerza la herramienta en código, salvo con dolor en los últimos mensajes.

### Pendiente, en este orden

1. **Medidas corporales con fecha**: peso, cintura, brazo, pecho, muslo y % de grasa.
2. **Pasos diarios desde Health Connect**:
   - Por qué así: Google Fit REST está en retiro y Health Connect no tiene API en la nube; los datos viven en el teléfono.
   - Qué hace falta: pasar el APK de TWA (PWABuilder) a un proyecto Android propio (Kotlin). Una tarea nocturna (WorkManager) lee los pasos del día y los manda a la API.
   - Costo en Azure: cero (una escritura por día).
   - Fuente de pasos: el teléfono o la app Google Fit, conectada a Health Connect.
   - Opcional: gym-coach escribe cada entreno en Health Connect como sesión de gimnasio (inicio, fin, duración), como hacía Hevy. Las series quedan solo en gym-coach.
   - Al dejar Hevy, quitarle los permisos en Health Connect para no duplicar sesiones. Diego tiene conectados Fit, Hevy y Claude.

Descartado: registro de comidas (el coach se enfoca en el entrenamiento).

---

## Orden de recursos de Azure por etapa

| Etapa | Recurso nuevo | Capa | Costo fijo |
|---|---|---|---|
| 1 | Static Web App, Cosmos DB | Free / free tier | No |
| 2 | Recurso + proyecto de Foundry, deployment de modelo mini | Pago por token | No |
| 3 | Agente en Agent Service | Pago por uso | No |
| 4 | Function App para MCP | Consumo | No |
| 5 | Azure AI Search | Free | No |
| 6 | Key Vault, Application Insights | Estándar / pago por GB con límite | No |
| 7 | — (usa el proyecto de Foundry) | Pago por token | No |

Ninguna etapa requiere un recurso con costo fijo. Si alguna lo necesitara, se propone aparte con su costo mensual y espera el OK de Diego.
