# Gemelo Digital — MVP Unity — Notas de desarrollo

Este documento registra cómo ha quedado estructurado cada sistema del MVP, a medida que se
implementa siguiendo el roadmap de 5 fases. Está pensado para que cualquiera (incluida una
futura sesión de Claude) entienda el diseño sin tener que releer todo el código.

Todo el código nuevo vive bajo `Assets/Scripts/`, organizado por namespace/carpeta:

```
Assets/Scripts/
  Core/                  Infraestructura común (bootstrap, indexado de escena, colliders, UI)
    UI/                  Mini-framework de UI sin EventSystem (ver más abajo)
  Navigation/            Fase 1 — tour por puntos
  Metadata/              Fase 2 — panel de metadatos al clicar
  IoT/                   Fase 3/4 — middleware MySQL y sensores en tiempo real
```

## Decisión de arquitectura transversal: todo se construye por código en tiempo de ejecución

No se ha tocado `MainScene.unity` a mano ni se han creado prefabs para la UI. En su lugar,
`Core/DigitalTwinBootstrap.cs` usa `[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]` para
levantar todos los sistemas (colliders, canvas, UI, managers) nada más arrancar la escena,
sin depender de ningún GameObject colocado manualmente.

Motivo: `MainScene.unity` tiene ~48.000 líneas de YAML y el modelo es una instancia de prefab
(el `.glb` importado con glTFast); editar ese archivo a mano para añadir GameObjects con
componentes correctamente referenciados (GUIDs de script, fileIDs únicos...) sin poder abrir
el Editor para verificar el resultado es un riesgo innecesario de corromper la escena. Con
`RuntimeInitializeOnLoadMethod` el sistema funciona igual (se auto-instala al pulsar Play o en
una build) y el riesgo desaparece. Como efecto colateral, tampoco han hecho falta prefabs ni
archivos `.meta` escritos a mano: Unity los genera solo al abrir el proyecto.

Por el mismo motivo (no poder abrir el Editor para probar visualmente) la UI se construye con
`UnityEngine.UI` clásico (`Text`, `Image`) posicionado con matemática manual en vez de
`VerticalLayoutGroup`/`ContentSizeFitter` o TextMeshPro: es más código, pero el resultado es
determinista y no depende de que el sistema de layout de uGUI se comporte como se espera sin
poder verlo. **Recomendación al abrir el proyecto en el Editor:** revisar visualmente tamaños,
márgenes y velocidades (todos son campos `public`/constantes fácilmente ajustables) y afinar a
gusto; es la parte que más se beneficia de iteración visual en el Editor.

### Mini-framework de UI sin EventSystem (`Core/UI/`)

El proyecto tiene **Active Input Handling = Input System Package** en
`ProjectSettings/ProjectSettings.asset` (`activeInputHandler: 1`), es decir, la clase legacy
`UnityEngine.Input` está desactivada. Los módulos estándar de UI de Unity
(`StandaloneInputModule`) dependen de esa clase, y `InputSystemUIInputModule` necesita un
asset de acciones que el proyecto no tiene. Para no depender de configuración de Editor no
verificable, se ha construido un router de clics propio:

- `ClickRouter`: cada botón/zona clicable de la UI se registra con un `RectTransform` y un
  callback. En cada frame comprueba si el puntero (leído de `UnityEngine.InputSystem`, no de
  `Input`) está sobre algún rect registrado y activo, y dispara el callback del que esté más
  arriba (`SortOrder`). También expone `IsPointerOverUI()`, que usan los sistemas de mundo
  (mirar alrededor, seleccionar elementos) para no "atravesar" la UI.
- `PointerGesture`: distingue clic (pulsar y soltar sin apenas mover el puntero) de arrastre,
  para que orbitar la cámara nunca dispare además una selección de elemento.
- `RuntimeUIFactory`: helpers para crear Canvas/paneles/texto/iconos por código, con una fuente
  interna del motor (`LegacyRuntime.ttf`) para no depender de importar TextMeshPro Essentials.
- `ManualScrollArea`: scroll vertical con rueda del ratón, sin `ScrollRect`.

---

## Fase 1 — Navegación por puntos (tour virtual)

**Archivos:** `Navigation/TourNavigationManager.cs`, `Navigation/TourCameraLook.cs`.

**Detección de puntos:** `Core/SceneModelIndex.cs` escanea todos los `IfcMetadata` de la
escena (componente inyectado por `Tools/IFC/Import Metadata`) y clasifica como punto de
navegación cualquiera con `ifcType == "IfcVirtualElement"` y `ifcName` empezando por
`"Esfera"` (37 encontrados en el modelo actual, `OrigenVLC_Sensores`). Sus `MeshRenderer` se
desactivan al arrancar: son marcadores de posición, no geometría real del edificio.

**Comportamiento:** al arrancar, la cámara se teleporta (sin transición) al punto "Esfera..."
más cercano a su posición inicial en la escena. A partir de ahí:

- **Mirar alrededor:** arrastrar con el ratón/dedo orbita la cámara en el sitio (yaw/pitch),
  sin desplazamiento — no es un controlador FPS (`TourCameraLook`).
- **Hotspots:** cada ~0.15s (`HotspotRefreshInterval`) se recalculan los puntos "cercanos y
  visibles" desde el punto actual: se descartan los que quedan tapados por geometría real
  (`Physics.Linecast` contra los `MeshCollider` añadidos por `ColliderBootstrapper`, ignorando
  la capa `IFCNavPoint` para que los propios marcadores no se bloqueen entre sí) y se muestran
  como iconos en pantalla (anillo + etiqueta con el nombre del punto), posicionados cada frame
  con `Camera.WorldToScreenPoint`. Si hay menos de `MinHotspotsAlwaysShown` (3) dentro del radio
  `MaxHotspotDistance` (15 m, **valor de partida a ajustar visualmente en el Editor** según la
  escala real del modelo, que no se ha podido verificar sin abrir Unity), se amplía a los más
  cercanos igualmente para no dejar nunca el tour sin salida.
- **Clic en un hotspot:** dispara `TravelTo()`, una corrutina de ~1.1s (`TransitionDuration`)
  que interpola la posición de la cámara (ease-in-out cúbico) hacia el punto destino, girando
  parcialmente (`TurnTowardsTravelBlend = 0.6`) hacia la dirección de desplazamiento.

**Por qué interpolación de posición y no fundido/corte:** a diferencia de un tour de fotos 360
(la referencia vtour.cloud), aquí hay geometría 3D real y continua. Mantener la cámara
desplazándose por el espacio (en vez de cortar o fundir a negro) conserva la orientación
espacial del operario dentro del edificio, que es precisamente lo que se quiere reforzar en un
gemelo digital de mantenimiento (evitar que el trabajador "se pierda" al saltar de punto en
punto). Es una decisión de diseño razonada, no una limitación técnica; si tras probarlo en
Editor se prefiere un fundido, el cambio está aislado en `TransitionRoutine()`.

**Limitación conocida:** los 37 puntos no tienen orientación propia en el IFC (son esferas,
sin "hacia dónde miran"), así que el giro en la transición se calcula solo a partir de la
dirección de viaje, no de una intención de encuadre del punto destino. Puede notarse en saltos
muy cortos o en ángulo. Ajustable con `TurnTowardsTravelBlend`.

---

## Fase 2 — Panel de metadatos al clicar

**Archivos:** `Metadata/ElementSelector.cs`, `Metadata/MetadataPanelController.cs`.

**Selección:** un clic (no arrastre, ver `PointerGesture`) sobre cualquier `MeshCollider` del
modelo lanza un `Physics.Raycast` y busca `IfcMetadata` en el objeto golpeado. Se ignoran los
puntos de navegación ("Esfera...", ya cubiertos por la Fase 1) y los clics en vacío cierran el
panel.

**Panel:** lateral derecho, construido en `MetadataPanelController.BuildPanel()`. Muestra
`ifcName`, `ifcType`, `ifcTag`, `globalId` y `hierarchyPath` en la cabecera, y todos los
`propertySets` (Psets) en una lista con scroll donde cada grupo es una cabecera desplegable
(clic para expandir/colapsar, todas colapsadas por defecto) con sus pares clave/valor debajo.

**Punto de extensión para la Fase 4:** `MetadataPanelController.SensorSectionBuilder` es un
delegado (`Func<IfcMetadata, RectTransform, float>`) que, si se asigna, se invoca al principio
del panel antes de los Psets, para insertar la sección de valores IoT en tiempo real sin tener
que modificar esta clase. Lo asigna `IoT/SensorIntegrationBootstrap.cs`.

---

## Fase 3 — Middleware de conexión en tiempo real Unity↔MySQL

**Archivo principal:** `IoT/MySqlSensorPollingService.cs` (+ `IoT/SensorCatalog.cs`, `IoT/SensorDataStore.cs`, `IoT/SensorModels.cs`).

**Decisión de arquitectura — sondeo (polling) directo por MySQL con `MySqlConnector`, no
WebSocket/MQTT.** Se ha podido tomar esta decisión sin bloquear el desarrollo porque, al
revisar `TFG/utility/`, había información suficiente para resolver la ambigüedad:

- El contenedor Docker `mysql-gemelo-digital` (ver `TFG/utility/informacion_mysql-gemelo-digital.txt`)
  solo expone el puerto 3306 (MySQL puro): no hay broker de mensajería ni API intermedia
  desplegada, ni se pedía montar una.
- `TFG/utility/mysqlconnector.2.6.1/` ya traía el paquete NuGet **MySqlConnector 2.6.1**
  descargado pero sin integrar en Unity — señal clara de que la conexión directa a MySQL desde
  Unity era la solución ya prevista para el proyecto. Se ha copiado el `.dll` de
  `lib/netstandard2.1/` (el perfil compatible con Unity) a `Assets/Plugins/MySqlConnector/`.
- `TFG/utility/origenvlc-sensores/periscoopedb.sql` (dump de la base de datos) dio el esquema
  exacto: tablas `sensors` (catálogo, con `ifc_sensor_global_id` = el mismo GlobalId que usa
  `IfcMetadata.globalId` en Unity — esa es la clave de vínculo entre un GameObject "EQE..." y su
  fila en MySQL), `sensor_rooms`, y cuatro tablas de lecturas por tipo
  (`temperature_sensor_readings`, `humidity_sensor_readings`, `pressure_sensor_readings`,
  `presence_sensor_readings`), todas con `sensor_id` + valor + `recorded_at`.

Con esquema, credenciales y librería ya resueltos, no quedaba ninguna decisión que solo el
usuario pudiera tomar razonablemente: montar un WebSocket/MQTT habría añadido infraestructura
nueva no solicitada para un requisito (paneles de mantenimiento, no control en milisegundos)
que un sondeo cada pocos segundos cubre de sobra, con mucha menos superficie que mantener en
un TFG.

### Dependencias del conector (paso obligatorio, si no el middleware no arranca)

`MySqlConnector` **no es autocontenido**. La build de `netstandard2.1` declara tres
dependencias en su `.nuspec` que hay que copiar también a `Assets/Plugins/MySqlConnector/`:

| Paquete | Versión |
|---|---|
| `Microsoft.Extensions.Logging.Abstractions` | 8.0.2 |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | 8.0.2 |
| `System.Diagnostics.DiagnosticSource` | 8.0.1 |

Copiar solo `MySqlConnector.dll` compila sin problemas —el compilador solo necesita los tipos
que se usan directamente— pero **falla en tiempo de ejecución** al instanciar la conexión:

```
TypeLoadException: Invalid type MySqlConnector.MySqlConnection for instance field
DigitalTwin.IoT.MySqlSensorPollingService+<PollOnceAsync>d__33:<connection>5__2
```

El mensaje despista, porque parece que el problema es el tipo `MySqlConnection` cuando en
realidad son sus dependencias: el runtime no puede terminar de cargar el tipo porque no
resuelve los ensamblados a los que hacen referencia sus miembros.

**Cómo obtenerlas:** descargar cada paquete de nuget.org (`https://www.nuget.org/api/v2/package/<nombre>/<versión>`),
renombrar el `.nupkg` a `.zip`, descomprimir y copiar el DLL de `lib/netstandard2.0/`.

**Requisito relacionado:** en Player Settings > Other Settings, **API Compatibility Level debe
ser .NET Standard 2.1**. Con ese perfil, Unity ya aporta `System.Memory`, `System.Buffers` y
`System.Runtime.CompilerServices.Unsafe`, que son dependencias transitivas de las anteriores;
con un perfil menor habría que añadirlas también a mano.

Si en el futuro aparecen más cascadas de dependencias, la alternativa robusta es instalar
*NuGetForUnity*, que resuelve el árbol completo automáticamente en vez de ir copiando DLLs.

**Cómo funciona:** `MySqlSensorPollingService` (MonoBehaviour, `DontDestroyOnLoad`) lanza un
bucle `async/await` (sin `ConfigureAwait(false)`, así todas las continuaciones vuelven al hilo
principal de Unity y no hace falta ningún lock) que cada `PollIntervalSeconds` (5 s por
defecto, campo público ajustable):

1. Carga el catálogo de sensores (`SensorCatalog`, tabla `sensors` + `sensor_rooms`) una sola
   vez la primera vez que consigue conectar.
2. Para cada una de las 4 tablas de lecturas, en el primer sondeo trae el último valor de cada
   sensor (para no arrancar el panel "en blanco"); a partir de ahí solo pide filas con
   `recorded_at > última_marca_de_agua_conocida`, así el coste de cada sondeo no crece con el
   histórico acumulado en la base de datos (a fecha del dump de referencia, la tabla de
   humedad ya tenía +66.000 filas).
3. Cada lectura nueva se guarda en `SensorDataStore`, indexada por `GlobalId` (no por
   `sensor_id`), y dispara `OnSensorUpdated(globalId)` si es más reciente que la que había.

Si la conexión falla (contenedor parado, credenciales cambiadas...) se registra un aviso una
sola vez en consola con el comando para levantar el contenedor, `IsConnected` pasa a `false`, y
el sondeo lo sigue reintentando solo cada ciclo — no hace falta reiniciar la app cuando el
contenedor vuelve a estar disponible.

---

## Fase 4 — Integración de sensores IoT

**Archivos:** `IoT/SensorPanelSection.cs`, `IoT/SensorIntegrationBootstrap.cs`.

Los sensores EQE... ya se detectan desde la Fase 1 (`SceneModelIndex.Sensors`, ver más arriba).
`SensorIntegrationBootstrap.TryAttach()` (llamado desde `DigitalTwinBootstrap`):

1. Crea el `MySqlSensorPollingService`.
2. Asigna `SensorPanelSection.Build` a `MetadataPanelController.SensorSectionBuilder` (el punto
   de extensión dejado preparado en la Fase 2): al clicar un sensor, el panel de metadatos
   muestra una sección extra en la parte superior — con fondo de color distinto — con el tipo
   de sensor, la sala, el valor actual formateado (°C / % HR / hPa / presencia sí-no) y la
   fecha de la última lectura. Si el `GlobalId` clicado no está en el catálogo de sensores
   (es decir, es un elemento normal del edificio), la sección no se dibuja: el panel de la
   Fase 2 no necesita saber nada de sensores para que esto funcione.
3. Se suscribe a `SensorDataStore.OnSensorUpdated`: si llega un valor nuevo del sensor que el
   panel tiene abierto en ese momento, lo refresca solo (`MetadataPanelController.RefreshIfShowing`)
   sin que el usuario tenga que volver a clicar — así se cumple "que se actualice en tiempo real
   cuando cambien en la base de datos" con el panel ya abierto.

**Estados que gestiona la sección del sensor:** valor con fecha de última lectura (caso normal);
"esperando la primera lectura" (sensor válido en el catálogo pero sin ninguna fila todavía);
"sin conexión con la base de datos" (con el último valor conocido si lo hay, en color de aviso).

---

## Fase 5 — Realidad Mixta sobre HTC Vive Focus Vision

**Archivos:** `MR/MRAnchorService.cs`, `MR/ModelAnchorBinder.cs`, `MR/MRDigitalTwinBootstrap.cs`.

**Cambio de rumbo respecto al plan inicial.** El diseño de partida
(`TFG/docs/roadmap/ADR-001-integracion-ar.md`) preveía AR Foundation sobre móvil con anclaje
por marcador impreso (`ARTrackedImageManager`). Al confirmarse que hay un **HTC Vive Focus
Vision** disponible se revisó el ADR, y al revisar qué extensiones OpenXR soporta ese
dispositivo apareció un dato que invalidaba la idea original: **no hay ninguna extensión de
seguimiento de imágenes**. Lo que sí ofrece son `XR_HTC_anchor` y `XR_HTC_anchor_persistant`
(anchors espaciales persistentes, disponibles en Focus Vision y XR Elite pero no en Focus 3).

Por eso el anclaje es **"colocar una vez y recordar"** en vez de "reconocer un marcador cada
vez": la primera ejecución pide al operario situar el modelo sobre el edificio real, se crea
el anchor y se persiste; las siguientes lo restauran solas. Además de ser lo que el hardware
permite, encaja mejor con el caso de uso (un operario no debería ir pegando marcadores).

**Estructura:**
- `MRAnchorService`: ciclo de vida del anclaje (soporte → adquirir colección persistida →
  restaurar si existe → si no, esperar colocación → crear y persistir). Expone `OnAnclado`
  con la pose y `OnEstadoCambiado`. Todas las llamadas al SDK están guardadas por
  comprobaciones de soporte, así que la escena sigue siendo abrible sin visor.
- `ModelAnchorBinder`: coloca el modelo. **El detalle que importa:** el anchor da una pose del
  mundo físico, pero el origen del modelo viene del IFC y no coincide con ningún punto notable
  del edificio; colocar el modelo directamente en la pose del anchor lo dejaría desplazado. Lo
  que se hace es calcular la transformación que lleva un **punto de navegación "Esfera..."**
  (referencia física reconocible) hasta la pose del anchor, y aplicarla a la raíz del modelo.
  Por defecto solo alinea el giro horizontal, para que el edificio no quede inclinado si el
  anclaje se crea con el mando torcido.
- `MRDigitalTwinBootstrap`: equivalente MR de `DigitalTwinBootstrap`. Diferencia de orden: en
  escritorio se puede indexar y navegar nada más cargar; aquí el modelo no tiene posición
  válida hasta que hay anclaje, así que es indexar → esperar anclaje → colocar → interactuar.

**Convivencia de los dos modos.** Ambos bootstraps se autoejecutan con
`RuntimeInitializeOnLoadMethod` en *cualquier* escena, así que cada uno comprueba el nombre de
la escena activa (`MRDigitalTwinBootstrap.NombreEscenaMR = "MRScene"`). Sin esa guarda, el modo
escritorio montaría su tour y su cámara encima del visor. `MainScene` se comporta exactamente
igual que antes de existir la Fase 5.

**Reutilización:** el middleware IoT (`IoT/*`) se usa **sin ningún cambio** — es totalmente
ajeno a cómo se renderiza la escena. El panel de metadatos también se reutiliza tal cual; su
adaptación a *world-space* (más natural en un visor que un panel fijo a pantalla) queda
pendiente de poder probarla con el dispositivo puesto.

**Pendiente en el Editor (no se puede hacer desde una shell):**
1. Crear `Assets/Scenes/MRScene.unity` con un XR Origin (su cámara con tag `MainCamera`) y el
   modelo importado, y añadirla a Build Settings.
2. Activar en Project Settings > XR Plug-in Management > OpenXR (pestaña Android) las features
   `VIVE XR Anchor`, `VIVE XR Passthrough` y `VIVE XR Composition Layer`.
3. Construir la UI de colocación del anclaje: algo que llame a
   `MRAnchorService.ColocarEnPose(pose del mando)` al confirmar. No se ha escrito porque
   depende de decisiones de interacción (¿qué botón? ¿rayo o mano?) que conviene tomar con el
   visor puesto.
4. Convertir materiales de VIVE a URP si hace falta: `Edit > Rendering > Materials > Convert
   Selected Built-in Materials to URP` (el SDK los trae para SRP; Unity 6 usa URP por defecto).
5. Known issue de HTC con OpenXR ≥1.12.1 en Android: visión recortada; se corrige poniendo
   `XRSettings.occlusionMaskScale = 0` y `useOcclusionMesh = false` al arrancar.

---

## Verificación end-to-end y limitaciones conocidas del MVP

**Aviso importante sobre el alcance de esta fase:** todo el desarrollo (Fases 1-4) se ha hecho
sin acceso a un Editor de Unity gráfico ni a un contenedor Docker en marcha en este entorno —
solo edición de archivos y una shell headless. La "prueba end-to-end" real (pulsar Play,
navegar, clicar, ver los sensores actualizarse) **queda pendiente de que se ejecute en tu
máquina**, que es donde están Unity y el contenedor `mysql-gemelo-digital`. Lo que sí se ha
hecho en esta fase es una revisión estática exhaustiva: los 18 scripts nuevos (~1800 líneas)
se han releído verificando balance de llaves/paréntesis, firmas de la API de Unity
(`Physics.Linecast`, `Object.FindObjectsByType`, `RectTransform` anchors/offsets, etc.),
coherencia de namespaces y del flujo de inicialización, y consistencia con el esquema real de
`periscoopedb.sql`. No sustituye a probarlo en el Editor, pero reduce mucho el riesgo de
errores de compilación o de referencias rotas la primera vez que se abra el proyecto.

### Checklist para probar en tu máquina

1. Abrir el proyecto en Unity 6000.3.16f1 y dejar que recompile (debería compilar sin errores;
   si algo no compila, lo más probable es un desajuste de versión de paquete, no de lógica).
2. `docker start mysql-gemelo-digital` antes de pulsar Play, si se quiere probar la Fase 3/4.
3. Pulsar Play sobre `MainScene`. En la consola debería verse la secuencia de logs de
   `[DigitalTwin]`: recuento de puntos de navegación/sensores, colliders añadidos, y (si el
   contenedor está arrancado) el catálogo de sensores cargado.
4. **Fase 1:** arrastrar para mirar alrededor; deberían aparecer 1-8 hotspots (anillo amarillo)
   hacia los puntos cercanos con línea de visión libre; clicar uno debe iniciar la transición.
5. **Fase 2:** clicar cualquier muro/puerta/mueble debe abrir el panel lateral con sus Psets
   colapsados; clicar una cabecera de Pset la despliega.
6. **Fase 4:** clicar un elemento "EQE_Sensor_..." debe mostrar además la sección azulada
   "SENSOR IoT · TIEMPO REAL" arriba del todo, con valor y fecha; si se deja el panel abierto y
   llega una fila nueva a la base de datos, el valor debería refrescarse solo.

### Ajustes que muy probablemente hará falta afinar visualmente en el Editor

- `TourNavigationManager.MaxHotspotDistance` (15 m de partida): no se ha podido verificar la
  escala real del modelo sin abrir Unity. Si aparecen demasiados o demasiado pocos hotspots,
  es el primer valor a tocar.
- Tamaño/posición del panel de metadatos (`MetadataPanelController`, constantes al principio
  de la clase) y velocidad de scroll (`ManualScrollArea.ScrollSpeed`): pensados para 1920x1080,
  pero sin poder ver el resultado real en pantalla.
- `TourNavigationManager.TransitionDuration` / `TurnTowardsTravelBlend`: la sensación de la
  transición (ver la explicación de la decisión de diseño en la Fase 1) es la parte más
  subjetiva del tour y la que más se beneficia de probarla en persona.

### Limitaciones conocidas

- **Sin AR todavía.** Este roadmap cubre navegación + metadatos + IoT en modo escritorio/3D de
  escritorio; la integración de AR Foundation sobre esta misma base (mencionada como siguiente
  pieza lógica en `TFG/CLAUDE.md`) no forma parte de este roadmap y queda para una fase
  posterior.
- **Rendimiento con MeshCollider no convexos:** `ColliderBootstrapper` añade un `MeshCollider`
  a cada objeto con malla que no tenga collider (varios cientos en este modelo). Es un coste
  único al arrancar la escena, pero si el modelo creciera mucho podría notarse en el tiempo de
  carga; en ese caso, lo primero a intentar sería generar los colliders una vez en el Editor
  (herramienta de menú) y guardarlos en la escena/prefab en vez de añadirlos en cada Play.
- **Sondeo, no push real.** El middleware de la Fase 3 consulta la base de datos cada
  `PollIntervalSeconds` (5 s por defecto); no hay un mecanismo de notificación instantánea
  (eso requeriría triggers + un canal de mensajería que MySQL no ofrece de fábrica). Para el
  caso de uso de un panel de mantenimiento es más que suficiente, pero si en el futuro hiciera
  falta latencia sub-segundo, ese sería el punto a rediseñar (ver la justificación completa en
  la Fase 3 más arriba).
- **`activeInputHandler` y EventSystem:** al no usarse `EventSystem`/`GraphicRaycaster` (ver
  Fase 1), no hay soporte de mando/teclado para navegar la UI, solo ratón/táctil. Si en el
  futuro se quiere accesibilidad por teclado o gamepad, habría que introducir
  `InputSystemUIInputModule` con un asset de acciones propio.
- **Nombre de sala como identificador del punto de navegación:** `TourNavigationManager.BuildDisplayName`
  usa el pset `Otros/LOC_Localizacion4` si existe; si ese campo falta o es ambiguo (varias
  esferas en la misma sala), el nombre mostrado será menos descriptivo (cae a "Punto {tag}").
- **Reconexión tras recarga de escena:** `DigitalTwinBootstrap` tiene una guarda para no
  duplicar los managers si `MainScene` se recarga en caliente durante la misma ejecución, pero
  el escenario "cargar una segunda escena distinta con más modelo IFC" no se ha probado.
