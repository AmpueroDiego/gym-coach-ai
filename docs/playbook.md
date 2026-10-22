# Playbook de gym-coach

Lecciones aprendidas y recetas para no repetir errores. Cada entrada: síntoma, causa, qué hacer.

## 1. Login con Google "entra y me bota" (producción)

- **Síntoma**: el usuario elige su cuenta, la app carga un instante y vuelve a la pantalla de login. Local funciona.
- **Causa**: Azure Static Web Apps **reemplaza la cabecera `Authorization`** por un JWT propio antes de llegar a la función administrada. `ValidadorGoogle` recibía un token que no era de Google, daba 401, y `api.ts` (401 → `cerrarSesion`) sacaba al usuario. El emulador local no pasa por SWA, por eso no fallaba.
- **Arreglo**: la PWA manda el token en `Authorization` **y** en `X-Custom-Authorization`; la API (`Http.Authorization`) lee primero la custom y usa `Authorization` solo si la custom falta o está en blanco. Si la custom viene con valor pero es inválida, **no** se cae al respaldo (401).
- **Regla**: en SWA con funciones administradas, nunca confiar en `Authorization` para auth propia.
- **Cómo se confirmó**: una petición sin cabeceras llegó a la API con `Authorization` ajena (motivo `SecurityTokenSignatureKeyNotFoundException`), y la custom con "basura" llegó intacta (`SecurityTokenMalformedException`).
- **Diagnóstico retirado**: se usó un motivo visible en el 401 (`X-Auth-Motivo` y `motivo` en el cuerpo, mostrado en el Login) y se quitó con `git revert` del commit `b593147`. Si el login vuelve a fallar, ese commit sirve de receta para volver a ponerlo. Se mantiene el log del motivo del rechazo (etiqueta fija, nunca token ni claims).

## 2. Cómo depurar un 401 sin logs

No hay Application Insights. Receta usada:
1. Verificar que el client ID del bundle (`curl` al JS publicado) y el app setting `GOOGLE_CLIENT_ID` coinciden. Leer solo esa clave: `az staticwebapp appsettings list ... --query "properties.GOOGLE_CLIENT_ID"` (el listado completo muestra `COSMOS_KEY`).
2. Reproducir con Playwright: poner un JWT falso con `sub` en `sessionStorage['gc.idToken']`, recargar, y revisar con `browser_network_request` qué cabeceras salen. No se puede iniciar sesión real con Google desde automatización (no se ingresan credenciales).
3. Si desde fuera no se ve el motivo, hacer visible una **etiqueta fija** del rechazo (excepción por tipo, nunca `ex.Message` ni claims) en el cuerpo del 401, y pedir al usuario que lea el texto.

## 3. Despliegue de la API

- **Usar GitHub Actions** (`.github/workflows/azure-static-web-apps.yml`): Oryx compila la API con .NET 10 y Azure registra las funciones.
- **No usar `swa deploy`** con la API publicada a mano: nunca registró funciones (404 vacío) y con `--api-version 10.0` el binario sale con código 1.
- `staticwebapp.config.json` no se copia solo a `dist`; debe estar en la carpeta que se despliega.
- Solo el push a `main` despliega (desde 2026-10-09; antes también `feature/**`). Flujo: feature → PR a `develop` → PR de `develop` a `main`.
- GitHub devolvió 500 transitorios (push de ramas nuevas y secretos) durante ~10 min: reintentar en bucle.
- Para verificar: `gh run watch <id> --exit-status`, y luego `curl` al sitio (bundle nuevo) y a `/api/ejercicios` (debe dar 401 `no_autenticado` sin token).
- Tras desplegar, la PWA puede servir la versión vieja por el service worker: probar con Ctrl+F5 o incógnito.

## 4. Google OAuth

- Origen autorizado exacto, sin `/` final ni ruta: `https://icy-glacier-07f29d20f.5.azurestaticapps.net`. Tarda minutos en aplicarse.
- Mientras la app esté en "Testing", la cuenta debe estar en **Usuarios de prueba** de la pantalla de consentimiento.
- El botón de GIS no necesita URIs de redireccionamiento.
- Un origen con un carácter extra (p. ej. una `}` al copiar) falla como `origin_mismatch`.
- El `VITE_GOOGLE_CLIENT_ID` entra al build por el workflow; el `GOOGLE_CLIENT_ID` de la API es un app setting. Deben ser el mismo.

## 5. Cosmos y costos

- **400 RU/s reales** (no 1000 como decía `CLAUDE.md`). Peor caso medido por usuario con los limitadores: ~70 RU/s, así que ~6 cuentas Google saturan la base. Uso normal: 1-2 RU/s.
- Rutinas viven en el contenedor `series` con id `rutina:<uuid>`: `PUT/DELETE /series/{id}` exigen UUID pelado, así que no pueden tocarlas.
- Opcional barato: excluir `/ejercicios/*` de la indexación (cambia un recurso de Azure; pedir OK).
- Cliente de Cosmos con `MaxRetryAttemptsOnRateLimitedRequests=3` y espera máxima de 5 s (antes sin tope: colgaba hasta ~30 s).
- **Costos medidos en el emulador (entrega A)**: PUT de serie ~10 RU (~20 si bate récord), DELETE ~8, `GET /rutinas` con 20 rutinas ~3.6, `GET /records` con 53 docs ~5.3, recálculo de récord ~3.5 + 0.036 RU/fila (81 con `TOP 2001`).
- **Perfil de entrenamiento y plan del día** (docs `perfil-entrenamiento` y `plan-dia:YYYY-MM-DD` en `series`). RU **estimadas; medir con `Medir_CostoDelPerfilDeEntrenamiento` (`ImportarIntegracionTests`) con el emulador**:

  | Operación | RU | Frecuencia |
  |---|---|---|
  | Recalcular perfil: consulta (806 series) | ~3.5 + 0.036 por fila: ~33-40 (tope 3001: ~115) | 1 al día por usuario + 1 tras importar |
  | Recalcular perfil: replace del doc (6-15 KB) | ~10-20 | igual |
  | Leer el perfil vigente | ~2-3 | cada GET y cada turno del coach |
  | Marcar en Importar (patch con filtro) | ~10-15 la primera vez, ~1-2 después (412) | por lote con series nuevas |
  | `mis_ejercicios` y la guardia de `proponer_rutina` | 0 (salen de `ctx`) | — |
  | Leer `plan-dia` | ~1 | cada turno y cada GET (sondeo cada 4 s mientras genera) |
  | Generar el plan (lease ~6-8, turno del modelo 10-60, replace ~8) | ~25-80 | **tope de 3 generaciones al día por usuario** |

  Muy por debajo de 400 RU/s; el peor pico es un recálculo de ~135 RU.
- **Topes anti-abuso**: 120 escrituras/min y 1500/día por usuario, 5 recálculos/min, `GET /series` rango 10/min, `GET /rutinas` 30/min, `GET /records` 60/min. Los limitadores son por instancia.
- **Récords** (`record:<ejercicioId>` en el contenedor `series`): se mantienen en el PUT/DELETE; si el mantenimiento falla se marca `desactualizado` ANTES de escribir la serie y la siguiente escritura o el GET lo recalculan. Un ejercicio con más de 2000 series queda con `excedeTope` (no se recalcula más). Un `AsyncLocal` no propaga hacia el caller; los semáforos por usuario (64 ranuras) tienen timeout de 5 s.

## 6. Validación en .NET

- `\p{C}` rechaza emojis: usar `[\p{Cc}\p{Co}\p{Cn}]` más bidi y ancho cero. ZWJ y VS16 pasan para no romper emojis compuestos.
- Claves JSON repetidas (`AllowDuplicateProperties=false`) y surrogates sueltos se rechazan con 400.

## 7. Trabajo con agentes y archivos

- **Incidente**: un script Python truncó `api/Validacion.cs` a 0 bytes (cambios sin commit). Se reconstruyó desde el transcript de subagentes sobre `git show HEAD:`. Lección: calcular el texto completo **antes** de abrir el archivo para escribir, evitar secuencias `\u` en heredocs, y commitear seguido.
- Verificar el id del agente antes de enviar un mensaje: dos pedidos de implementación se mandaron por error al agente de QA.
- Cambios de auth: `/orquestar` con `coder` en Opus y `adversary`. Un cambio de 2-3 archivos con contrato claro no necesita planner.
- Un `AsyncLocal` seteado en un método async callee **no** se propaga al caller: para pasar un dato (p. ej. un motivo) hasta la respuesta, devolver una tupla.
- Después de cada despliegue, comprobar con `curl` en vez de asumir.

## 8. Coach (etapa 2)

- **Deuda (etapa 6)**: sin identidad administrada. Las Functions gestionadas de la SWA Free no la soportan y las referencias a Key Vault exigen SWA Standard (~9 USD/mes), así que `AZURE_OPENAI_KEY` vive en App Settings. Al pasar a Functions propias: identidad administrada y borrar la key.
- **Capacidad del deployment `coach-mini`: 50 (50 RPM / 50k TPM).** Con 10 (10 RPM / 10k TPM) una asesoría (~11k tokens, 3 llamadas) daba 429 y el SDK esperaba el `Retry-After` (~30 s): turnos de 33-67 s. Subirla no cuesta (Global Standard se paga por token); el gasto lo frenan el tope de 20 mensajes/día y la alerta de 5 USD. Tras cambiarla, Azure tarda unos minutos en aplicarla: verificar con la cabecera `x-ratelimit-limit-requests`. Medido con 50: asesoría 6-8 s, pregunta simple ~4 s.
- **Topes del coach** (en memoria, por instancia): 5 mensajes/min y 20/día por usuario; 6 ejecuciones de herramientas y 5 rondas por mensaje; el coach tiene 6 herramientas (`ver_entrenos_recientes`, `progreso`, `volumen_por_grupo`, `mis_ejercicios`, `recordar`, `proponer_rutina`); 38 s de timeout en backend (SWA corta a los 45 s; la PWA espera 45 s). "Tu entreno de hoy" midió ~41 s con el modelo real antes de pedir las herramientas en paralelo. Un 429 de Azure OpenAI nunca sale como 429: la PWA ve 503 `no_disponible`.
- **No loguear el texto** de mensajes ni respuestas (son datos personales y salud): solo rondas, nombres de herramientas y tokens.
- `az staticwebapp appsettings set ... -o none`: sin `-o none` la salida imprime todos los settings, key incluida.
- gpt-5-mini exige `reasoning_effort` (`AZURE_OPENAI_REASONING=minimal`) y no admite fijar temperatura. **Se retira el 2027-02-09**: reemplazo `gpt-5.4-mini`; basta crear otro deployment y cambiar `AZURE_OPENAI_DEPLOYMENT`, y correr `docs/coach-casos.json` antes de cambiar.
- **Idioma**: `POST /coach` y `/coach/plan-dia` aceptan `idioma` (`es`|`en`, ausente = `es`, otro valor = 400). En `en` el prompt solo cambia dos frases (las reglas siguen en español; en `es` queda idéntico). El plan del día guarda `idioma` y al cambiarlo se **traduce** (reemplaza la variación actual con los mismos ejercicios; no gasta una de las 3) con **un intento por día** (`regeneradoPorIdioma`, se marca si el modelo respondió aunque saliera mal), con el mismo lease y cupos; si no se puede, devuelve el que había.
- Los casos del coach (`CoachCasosTests`) usan el modelo real: no entran en la corrida rutinaria, se saltan sin `AZURE_OPENAI_*`.

## 9. Comprobación rápida antes de decir "listo"

1. `npx vitest run`, `npm run lint`, `npm run build` en `app`.
2. `dotnet test api.Tests --filter "FullyQualifiedName!~Integracion&FullyQualifiedName!~CoachCasos"` (las de integración necesitan el emulador de Cosmos; los casos del coach, además, el modelo).
3. Push, `gh run watch`, `curl` al bundle y a `/api/*`.
4. Prueba real del usuario con login de Google.

## 10. Sesión por cookie

- **Qué es**: `POST /api/sesion` canjea el ID token de Google por una cookie `__Host-gc_sesion` de 90 días (`Max-Age=7776000; Path=/; Secure; HttpOnly; SameSite=Lax`). Sin estado en el servidor: 0 RU, $0. `GET /api/sesion` la verifica y la renueva si le quedan menos de 45 días; `DELETE /api/sesion` la borra. Código en `api/Auth/SesionFirmada.cs`, `api/Auth/Autenticador.cs` y `api/Functions/Sesion.cs`.
- **Formato**: `v1.<payload>.<mac>` en base64url sin relleno. Payload JSON `{"sub","nombre","foto","exp"}` (sub de Google sin `google:`); MAC = HMAC-SHA256 con `SESSION_SECRET` sobre `gc_sesion.v1.<payload>`. El JSON se parsea solo con el MAC válido. Nunca loguear el valor: solo etiquetas fijas (`cookie_formato`, `cookie_firma`, `cookie_vencida`, `cookie_duplicada`, `csrf`, `otra_cuenta`).
- **Auth de las funciones de datos** (`Autenticador.UserIdAsync(req)`): sin cookie, el flujo de siempre con el token en `X-Custom-Authorization`. Con cookie: inválida o duplicada → 401 sin caer al header; escrituras (POST/PUT/PATCH/DELETE) exigen `X-Requested-With` (CSRF); si viene `X-Gc-Sub` y no coincide con la cookie → 401 (cola vieja de otra cuenta en el mismo navegador).
- **Sin `SESSION_SECRET` la API no arranca** (como sin `GOOGLE_CLIENT_ID`): todas las funciones caen, no solo `/api/sesion`. Debe ser base64 de al menos 32 bytes. Crear el app setting **antes** de desplegar el backend, sin imprimirlo:
  ```powershell
  $b = New-Object byte[] 48; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
  az staticwebapp appsettings set -n swa-gym-coach -g rg-gym-coach --setting-names "SESSION_SECRET=$([Convert]::ToBase64String($b))" -o none
  ```
  (En pwsh 7 sirve también `[Security.Cryptography.RandomNumberGenerator]::GetBytes(48)`; en Windows PowerShell 5.1 ese método no existe.)
- **Rotación**: poner el valor actual en `SESSION_SECRET_ANTERIOR` y uno nuevo en `SESSION_SECRET`. Las cookies viejas siguen valiendo y el siguiente `GET /api/sesion` las reemite con el secreto nuevo; pasados 90 días, borrar `SESSION_SECRET_ANTERIOR`. Para **revocar todas** las sesiones (no hay revocación por usuario): rotar sin `SESSION_SECRET_ANTERIOR`; todos vuelven a iniciar sesión.
- **Local sin https**: `SESSION_COOKIE_INSECURE=1` quita `Secure` y el prefijo (el prefijo `__Host-` exige `Secure`): la cookie se llama `gc_sesion`. Solo tiene efecto si además `AZURE_FUNCTIONS_ENVIRONMENT=Development` (lo pone Core Tools); en Azure se ignora. Al arrancar así, la API deja un warning en el log.
- **Verificar en producción** (tras desplegar el backend; bash). El ID token se copia de DevTools → Network, en el body del `POST /api/sesion` de un login real (dura 1 h; no pegarlo en chats ni archivos versionados):
  ```sh
  U=https://icy-glacier-07f29d20f.5.azurestaticapps.net
  J=$(mktemp)                       # tarro de cookies fuera del repo
  read -rs TOKEN                    # pegar el idToken: -s para que no quede en pantalla ni en el historial
  # 1. Sin X-Requested-With → 403 csrf; token basura → 401 sin Set-Cookie.
  curl -si -X POST $U/api/sesion -H 'Content-Type: application/json' -d '{"idToken":"x"}' | head -1
  curl -si -X POST $U/api/sesion -H 'X-Requested-With: gym-coach' -H 'Content-Type: application/json' -d '{"idToken":"x"}' | grep -iE '^HTTP|set-cookie'
  # 2. Token real → 200 y Set-Cookie __Host-gc_sesion con Secure; HttpOnly; SameSite=Lax intactos.
  curl -si -c $J -X POST $U/api/sesion -H 'X-Requested-With: gym-coach' -H 'Content-Type: application/json' -d "{\"idToken\":\"$TOKEN\"}" | grep -iE '^HTTP|set-cookie'
  # 3. SWA reenvía la cookie: 200 {sub,nombre,foto} y 200 en datos sin ningún header de auth.
  curl -si -b $J $U/api/sesion | head -1
  curl -si -b $J $U/api/ejercicios | head -1
  # 4. Escritura con cookie sin X-Requested-With → 401.
  curl -si -b $J -X DELETE $U/api/series/00000000-0000-0000-0000-000000000000 | head -1
  # 5. OBLIGATORIO al final: borrar la sesión y el tarro.
  curl -si -b $J -X DELETE $U/api/sesion -H 'X-Requested-With: gym-coach' | grep -iE '^HTTP|set-cookie'   # 204 + Max-Age=0
  rm -f $J
  ```
  El paso 5 no es opcional: el servidor no guarda sesiones, así que la cookie del tarro valdría 90 días para la cuenta real aunque el DELETE responda 204. Borrar el archivo es lo que la anula de verdad.
- **Si SWA no reenvía la cookie** (el POST da 200 con Set-Cookie pero `GET /api/sesion` da 401 `sin_sesion`, o el Set-Cookie llega sin atributos o no llega): **no publicar el frontend nuevo**. El backend sigue aceptando el token en `X-Custom-Authorization` cuando no hay cookie, así que la PWA anterior funciona igual. Investigar con la receta de la sección 2 antes de seguir.

## 11. Coach como agente (etapa 3)

- **Sincronizar el agente** (después de tocar `PromptCoach` o las herramientas, antes de fusionar a `main`; `az login` y rol "Foundry User" en el proyecto): `dotnet run --project herramientas/SincronizarAgente` crea una versión de `gym-coach` solo si cambió el hash (SHA-256 de instrucciones, entradas, herramientas, modelo y razonamiento, guardado en `metadata.hash`). `-- --comprobar` solo informa (exit 1 si hace falta sincronizar; exit 2 = error, flag desconocido o `--borrar` sin `--agente` distinto del real). Endpoint por `--endpoint` o `FOUNDRY_PROJECT_ENDPOINT`; agente por `--agente`. Usa REST directo: `Azure.AI.Projects` 2.0.1 va contra OpenAI 2.9.1 y choca con el 2.14 de la API (`MissingMethodException`). No compilar la API y el sincronizador en paralelo (comparten `api/obj`).
- **Auth con key**: el endpoint del proyecto acepta la api-key de `aif-gym-coach-diego` aunque la doc diga solo Entra (verificado 2026-10-20). Se reusa `AZURE_OPENAI_KEY`: sin service principal ni secreto nuevo.
- **Rol renombrado**: "Azure AI User" ahora es "Foundry User". En Git Bash, `az role assignment` con scopes que empiezan con `/subscriptions/...` necesita `MSYS_NO_PATHCONV=1` (si no, MSYS convierte la ruta).
- **Borrar una conversación no borra sus responses**, y estas guardan las instrucciones renderizadas con los DATOS del usuario: hay que borrarlas con `DELETE /responses/{id}`.
- **App Settings nuevos** (sin secretos): `FOUNDRY_PROJECT_ENDPOINT` (`https://aif-gym-coach-diego.services.ai.azure.com/api/projects/gym-coach`), `COACH_AGENTE=gym-coach`, `COACH_MOTOR=agente|bucle`. Con `az staticwebapp appsettings set ... -o none`.
- **Camino de vuelta**: `COACH_MOTOR=bucle` (también el default) usa el bucle de la etapa 2 sin desplegar código.
- **Aislamiento**: la key ve todo el proyecto (todas las conversaciones). Lo garantiza la API: el `conversacionId` sale solo del documento del `userId` del token, nunca del body ni de la respuesta. Deuda de la etapa 6: pasar a identidad/Entra.
- **Limpieza manual semanal**: las Functions de SWA no tienen timer, así que las conversaciones y responses de usuarios que no vuelven se quedan en Foundry. Cada semana: `dotnet run --project herramientas/SincronizarAgente -- --limpiar` (simulación: solo cuenta) y luego `-- --limpiar --si` para borrar lo de más de 2 días (`--dias N` lo cambia; `--dias 0` borra todo, úsalo solo en pruebas). Borra primero las responses y luego las conversaciones, en paralelo de 8, y solo imprime conteos.
- **Interruptor ante caída de Foundry**: `COACH_MOTOR=bucle` en App Settings. Si solo falla un turno, el fallback usa el bucle sin memoria y solo invalida conversaciones rotas (no las sanas).
- **Radio de daño**: la key de Azure OpenAI ahora da acceso a todas las conversaciones y responses del proyecto (con DATOS de usuarios). Si se filtra, rotarla y limpiar.
