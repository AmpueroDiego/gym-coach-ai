# gym-coach — instrucciones del proyecto

Proyecto personal de Diego: app de gym (PWA) para registrar entrenos, con un coach de IA que analiza el historial real, recomienda y conversa.

## Visión del producto

El coach vale por una sola cosa: **recomienda con los números reales del usuario**, no con consejos genéricos. "Subiste 7.5 kg en banca en 6 semanas pero tu volumen de espalda bajó" — no "recuerda entrenar espalda".

Ante dos diseños igual de correctos, gana el que deja registrar una serie en el gym con menos toques (una mano, entre series, con el celular).

## Stack

| Pieza | Tecnología |
|---|---|
| Frontend | React + TypeScript, PWA, Azure Static Web Apps |
| Backend | Azure Functions (plan de consumo, serverless) — lenguaje por decidir |
| Datos | Azure Cosmos DB for NoSQL, **free tier** (400 RU/s reales en esta cuenta, 25 GB), partición `/userId` |
| Coach | Agente en Microsoft Foundry (Azure OpenAI, modelo mini) |
| Herramientas del coach | Servidor MCP en Azure Functions |
| RAG | Azure AI Search (capa Free); plan B: búsqueda vectorial de Cosmos |

Suscripción de Azure: `AlabastroMX` (personal de Diego). Región por defecto: `eastus`.

## Reglas de diseño

- **Los entrenos no van por RAG.** Son datos estructurados: el coach los consulta con herramientas MCP (`ver_entrenos_recientes`, `progreso`, ...). El RAG es solo para conocimiento de entrenamiento y notas libres.
- **El coach no inventa datos**: todo número que diga sale de una herramienta que consultó. Ante dolor o lesión, recomienda ver a un profesional.
- **Barato primero.** Antes de crear un recurso de Azure, confirmar su capa gratis o costo mensual estimado. Nada de recursos con costo fijo (App Service dedicado, SQL provisionado, etc.) sin pedido explícito.
- **Cosmos free tier**: la cuenta de Diego tiene **400 RU/s reales** compartidos (no 1000), a nivel de base de datos. ~6 cuentas Google saturan la base. Ver `docs/playbook.md`.
- **Validación en el backend**, no solo en la PWA.
- KISS y YAGNI: es un proyecto de una persona. Sin capas ni abstracciones que no pague una necesidad real.

## Secretos

Prohibido versionar claves, cadenas de conexión o endpoints con key. Local: `local.settings.json` (en `.gitignore`); Azure: Application Settings / Key Vault.

## Flujo de trabajo

- Flujo feature → develop → main en el repo privado `AmpueroDiego/gym-coach`: ramas `feature/<slug>` o `fix/<slug>` desde `develop`, PR a `develop`; para publicar, PR de `develop` a `main`.
- Solo `main` despliega a producción (Azure Static Web Apps, vía GitHub Actions).
- Conventional commits en español (`feat:`, `fix:`, `chore:`, `docs:`).
- Cambios de código vía `/orquestar` (planner → coder → qa → adversary solo si el cambio toca datos, auth o el coach).
- Crear o borrar recursos de Azure: proponer el comando exacto y esperar el OK de Diego.

## Etapas

Detalle, costos y criterio de "listo" de cada etapa en `docs/roadmap.md`.

Lecciones y recetas (login, despliegue, cuentas de GitHub, depuración): `docs/playbook.md`.

1. Registrar entrenos (sin IA)
2. Coach con el catálogo de modelos de Foundry (modelo mini + function calling sobre los datos)
3. Coach como agente en Foundry Agent Service ← **actual**
4. Herramientas como servidor MCP (catálogo de herramientas; también conectable a Claude Desktop)
5. RAG con Foundry IQ (guías de entrenamiento y notas)
6. Seguridad y gobierno completos (el mínimo entra desde la etapa 2)
7. Evaluación del coach (unos casos de prueba desde la etapa 2)
