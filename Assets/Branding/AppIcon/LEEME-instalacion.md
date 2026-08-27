# Icono de aplicación — instalación en Unity

Juego completo para el APK de `ARScene` (perfil **HTC Vive Focus Vision**, Android).
Todos los ficheros son PNG con alfa, generados uno a uno al tamaño final
(no son reescalados de un único máster: el grosor de trazo está corregido
ópticamente en cada tamaño y el detalle fino se retira por debajo de 96 px).

## 1. Importar

Copiar la carpeta entera a `Assets/Branding/AppIcon/`. Unity generará los `.meta`
al abrir el editor. No hace falta tocar los ajustes de importación.

## 2. Asignar en Player Settings

`Edit > Project Settings > Player > Android > Icon`

| Sección del panel | Tamaño | Fichero |
|---|---|---|
| **Adaptive** — Background | 432 / 324 / 216 / 162 / 108 | `adaptive_background_<n>.png` |
| **Adaptive** — Foreground | 432 / 324 / 216 / 162 / 108 | `adaptive_foreground_<n>.png` |
| **Round** | 192 / 144 / 96 / 72 / 48 | `round_<n>.png` |
| **Legacy** | 192 / 144 / 96 / 72 / 48 | `legacy_<n>.png` |

Los otros dos ficheros no van en Player Settings:

- `icono_master_1024.png` — máster de presentación (memoria, diapositivas, README).
- `icono_playstore_512.png` — 512×512 sin transparencia, formato de ficha de tienda.

## 3. Comprobaciones ya hechas

- **Zona segura del icono adaptativo:** la envolvente del símbolo ocupa **0,581**
  del lienzo de 108 dp, por debajo del límite de Google (66/108 = 0,611). Sea cual
  sea la máscara del lanzador —círculo, squircle, cuadrado redondeado— no se recorta nada.
- **Contraste sobre el fondo `#16181C`:** estructura `#E8EEF8` 15,25:1;
  cara iluminada `#5A84CC` 4,75:1; señal `#8FB4EE` 8,41:1.
- **Prueba de reducción:** el símbolo mantiene estructura y jerarquía a 48 px
  (ver la banda inferior de la lámina de identidad).

## 4. Coherencia con la pantalla de presentación

La paleta arranca del `#16181C` que ya fijaste para el splash del visor el 17-08,
y el azul `#223D71` es el institucional muestreado del propio
`Assets/Branding/logo_unizar.png`. El icono y el splash comparten fondo.

## 5. Aviso sobre git

Los `.png` están declarados en `.gitattributes` como LFS-tracked, así que **hay que
añadirlos al repositorio desde una máquina con `git-lfs` instalado**, igual que se
hizo con `logo_unizar_negativo.png` y `splash_vr_unizar.png`.
