# Despliegue — instalar y ejecutar sin abrir Unity

Cómo poner en marcha la aplicación **a partir de los binarios ya compilados**. No hace falta Unity
ni compilar nada. Para el diseño interno del sistema, ver `DEV_README.md`.

Hay dos aplicaciones que salen del mismo proyecto: **escritorio** (Windows) y **visor** (HTC Vive
Focus Vision, APK de Android). Las dos leen la telemetría del mismo servidor.

---

## Lo que hay que entender antes de nada

La aplicación **no trae dentro la dirección de la base de datos**. La lee de un fichero externo,
`backend.json`, que se coloca junto al ejecutable. Está hecho así a propósito: entre la
instalación y la defensa la dirección del servidor puede cambiar, y recompilar exigiría un
ordenador con Unity, el proyecto abierto y el visor conectado por cable — tres cosas que no hay en
una sala de defensa. Con el fichero externo, cambiar de servidor es copiar veinte líneas.

Si `backend.json` no está, la aplicación **arranca igualmente** y el modelo es navegable, pero cae
a los valores compilados —que apuntan a `127.0.0.1`, o sea al propio dispositivo— y los paneles de
sensores salen vacíos.

`backend.json` **contiene una contraseña**, así que no se versiona: está en el `.gitignore`. La
plantilla vive en `TFG/utility/backend-nube/backend.json.plantilla`.

---

## Escritorio (Windows)

1. Copia la carpeta de la compilación entera a donde quieras. Es portable, no se instala nada.
2. Pon `backend.json` **junto al `.exe`**, no dentro de la carpeta `_Data`.
3. Ejecuta `Gemelo Digital BIM.exe`.

Controles: ratón para mirar, `WASD` para desplazarse, clic para seleccionar un elemento y ver sus
metadatos y su última lectura. `Esc` abre el menú de ajustes.

---

## Visor (HTC Vive Focus Vision)

Con el visor conectado por USB y la depuración por USB activada (*Ajustes → Avanzado*).

### Instalación completa

```powershell
# 1. Quitar la versión anterior
adb uninstall es.unizar.eupt.gemelodigital

# 2. Instalar la nueva
adb install -r HTC-Vive-Focus-Vision-Digital-Twin.apk

# 3. Empujar la configuración  <-- NO TE SALTES ESTE PASO
adb push backend.json /sdcard/Android/data/es.unizar.eupt.gemelodigital/files/backend.json

# 4. Comprobar que ha llegado entero
adb shell ls -l /sdcard/Android/data/es.unizar.eupt.gemelodigital/files/backend.json
```

> ### ⚠ La trampa que se lleva a todo el mundo
>
> **Desinstalar borra `backend.json`**, porque vive dentro del directorio de datos de la
> aplicación. Si reinstalas y no repites el paso 3, la aplicación arranca con normalidad, el
> modelo se ve bien y **no hay telemetría**, sin ningún mensaje de error visible. Ha pasado ya dos
> veces en este proyecto y las dos costaron un buen rato de diagnóstico.
>
> La regla es corta: **desinstalar, instalar, empujar.** Siempre los tres, siempre en ese orden.

### Solo cambiar de servidor

Si únicamente se ha mudado la base de datos, basta el paso 3. **No hay que reinstalar ni
recompilar.**

### Lanzar la aplicación

Desde la **Biblioteca** del visor, cambiando el filtro para ver las aplicaciones instaladas por el
usuario. Si no aparece, comprueba que *Ajustes → Avanzado → Permitir aplicaciones desconocidas*
está activado, y en último caso lánzala desde el PC:

```powershell
adb shell monkey -p es.unizar.eupt.gemelodigital -c android.intent.category.LAUNCHER 1
```

### Sin cable

Para instalar por Wi-Fi, una vez con cable:

```powershell
adb tcpip 5555
adb shell ip addr show wlan0
adb connect <IP-DEL-VISOR>:5555
```

A partir de ahí valen los mismos comandos sin cable. Se pierde al reiniciar el visor.

**Para la sesión de uso no hace falta cable ni conexión**: el visor es autónomo y solo necesita
red para la telemetría.

---

## Comprobar que la telemetría funciona

Dentro de la aplicación, abre cualquier sensor y mira la línea de estado del panel:

| Lo que dice | Qué significa |
|---|---|
| «Última lectura» con la fecha de hoy | Todo bien. Espera diez segundos y el valor cambia |
| «Última lectura» con una fecha vieja | La base de datos responde pero el simulador está parado |
| «Sin conexión con la base de datos» | No hay red, o el servidor está caído, o falta `backend.json` |

Esperar y ver cambiar el valor es lo único que demuestra la cadena entera: simulador → base de
datos → Internet → dispositivo → panel.

---

## Cuando no hay telemetría

En este orden, que va de lo más probable a lo menos:

**1. ¿Está el fichero?** Es la causa más frecuente con diferencia.

```powershell
adb shell ls -l /sdcard/Android/data/es.unizar.eupt.gemelodigital/files/backend.json
```

**2. ¿Qué configuración cree la aplicación que está usando?** El registro lo dice sin ambigüedad:

```powershell
adb logcat -d -s Unity | Select-String "backend|IoT"
```

`Configuración de backend leída de /storage/...` significa que el fichero está bien y el problema
está en el servidor. `Se usan los valores compilados` significa que el fichero falta, o que no es
JSON válido.

> **Ojo con la codificación.** Si generas `backend.json` desde PowerShell con `>`, sale en UTF-16 y
> la aplicación no puede leerlo: dirá que el fichero existe pero no ha podido aplicarse. Tiene que
> ser UTF-8. Y `findstr` tampoco lee UTF-16, así que para buscar en volcados usa `Select-String`.

**3. ¿Responde el servidor?** Desde el PC, no desde el propio servidor:

```powershell
Test-NetConnection <nombre-del-servidor> -Port 3306
```

**4. ¿Están los contenedores en pie?** Por SSH en la máquina: `docker ps` debe mostrar dos en
`Up`, y `docker logs --tail 3 simulador-iot` marcas de hace segundos, no de hace horas.

El procedimiento completo de despliegue del servidor está en
`TFG/docs/roadmap/DESPLIEGUE-nube-y-publicacion.md`.

---

## Diagnóstico dentro del visor

No hay consola ni depurador: `adb logcat` es la única forma de saber qué pasa ahí dentro.

```powershell
# ANTES de lanzar la aplicación
adb logcat -G 16M     # agranda el búfer
adb logcat -c         # lo vacía

# ... usar la aplicación con normalidad, con cable o sin él ...

# DESPUÉS, y antes de apagar o reiniciar el visor
adb logcat -d -v threadtime > log-AA-MM-DD-HHMM.txt
```

Tres avisos que ya han costado sesiones enteras: **reiniciar el visor vacía el búfer** y se lleva
la sesión; **volcar demasiado pronto** deja fuera lo que interesa, porque el middleware de sensores
arranca después de elegir modo; y **reutilizar el nombre del fichero** sobrescribe el volcado
anterior sin avisar — por eso el nombre lleva la hora.

Todo lo propio va con el tag `Unity` y prefijo entre corchetes: `[DigitalTwin]`,
`[DigitalTwin][AR]`, `[DigitalTwin][IoT]`. Detalle completo en `TFG/utility/informacion_logcat.txt`.
