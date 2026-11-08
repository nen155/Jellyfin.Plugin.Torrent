# Torrent Search para Jellyfin

Plugin para **Jellyfin 12.x (net10.0)** que hace de **proxy entre tus búsquedas
y las webs de torrents que tú configures**, y envía los enlaces al cliente de
descargas de tu equipo.

No trae webs "hardcodeadas": cada fuente se define con su URL de búsqueda y sus
selectores CSS, su API JSON o una API Torznab. Además puede **abrir la web en un
navegador real (Chrome/Edge headless)** para ejecutar su JavaScript, resolver
retos anti-bot y pulsar los botones de descarga protegidos.

## Qué hace

- **Búsqueda agregada** en todas las fuentes activas en paralelo, con caché y
  aislamiento de errores (si una web falla, las demás siguen).
- **Tres motores de carga por fuente**:
  - `HTTP`: petición simple (rápida).
  - `Navegador`: Chrome/Edge headless controlado por CDP; ejecuta el JS de la
    web, espera selectores, ejecuta tus scripts y **captura las peticiones de
    red** (magnet y `.torrent` que genera la propia página).
  - `Automático`: HTTP y, si la web bloquea o exige JavaScript, navegador.
  - Opcionalmente **FlareSolverr** como respaldo para retos de Cloudflare.
- **Extracción de todo lo que publica la web**: magnet y `.torrent`, semillas,
  clientes, descargas completadas, idioma (castellano, latino, VOSE, multi…),
  resolución, medio, códecs de vídeo y audio, canales, HDR/Dolby Vision, remux,
  3D, contenedor, grupo, temporada/episodio, tamaño, fecha, carátula, sinopsis y
  los pares de datos de la ficha (Año, Género, Dirección, Reparto, Formato,
  Duración…).
- **Envío al cliente**: qBittorrent (WebUI), Transmission (RPC), Deluge (Web) o
  modo automático que prueba los tres. Si la web protege la descarga, el plugin
  descarga el `.torrent` **con las cookies de la sesión del navegador** y lo
  sube al cliente como archivo.
- **Resolución automática de dominio** (página HTTP o canal de Discord con bot).
- **Fuentes Torznab** (Jackett, Prowlarr, trackers con API compatible).
- **Plantillas** listas: Nyaa, WatchSoMuch, DonTorrent, 1337x, YTS, Torznab,
  genérica HTML y genérica JSON.

## Instalación

```powershell
dotnet publish -c Release -o out
```

Copia el contenido de `out\` a `<datadir>/plugins/TorrentSearch/` y reinicia
Jellyfin. En este equipo:

```
C:\Users\<usuario>\AppData\Local\jellyfin\plugins\TorrentSearch\
```

Páginas disponibles:

- **Buscar torrents** (menú lateral del panel).
- **Torrent Search** (Panel → Plugins → Torrent Search → Ajustes).

## Configurar una fuente HTML

| Campo | Ejemplo | Notas |
|---|---|---|
| URL base | `https://nyaa.si` | Sin barra final |
| Motor | `HTTP` / `Navegador` / `Automático` | El navegador hace falta si el buscador es JS |
| URL de búsqueda | `{base}/?f=0&c=0_0&q={query}&p={page}` | Admite `{base}`, `{query}` y `{page}` |
| Método | `GET` / `POST` | Con POST: cuerpo `valor={query}` |
| Selector de fila | `table.torrent-list tbody tr` | Un resultado por elemento |
| Título | `td a[href^='/view/']:not(.comments)` | Si hay varias coincidencias se usa el texto más largo |
| Enlace a la ficha | `td a[href^='/view/']@href` | `@atributo` lee un atributo |
| Magnet / .torrent | `a[href^='magnet:']@href` | Si no están en la lista, se buscan en la ficha |
| Semillas / clientes / completadas | `td:nth-child(6)` … | |
| Idioma / fecha / carátula | `.language@title`, `.releaseDate@title`, `img@src` | |

Los selectores admiten **`selector@atributo`** (`img@src`, `a@href`). Si `src`
está vacío se prueban `data-src`, `data-original` y `data-lazy-src`.

### Cuando la web necesita JavaScript

En la sección **Navegador (JavaScript)** de la fuente:

- **Esperar este selector**: la página se considera lista cuando aparece (por
  ejemplo `.MediumMovie`). Es la forma más fiable y rápida.
- **JavaScript en la búsqueda**: se ejecuta tras cargar la URL; `{query}` se
  sustituye por el texto buscado. Sirve para rellenar un buscador y lanzarlo.
- **Motor de la ficha** y **JavaScript en la ficha**: permiten pulsar el botón
  de descarga de la web y capturar el enlace que genera (por ejemplo, tras un
  proof-of-work).
- **Abrir los enlaces de descarga intermedios**: si la ficha enlaza a
  `/Download-123/release` en lugar del `.torrent`, el plugin abre esas páginas y
  extrae el magnet de cada una (es lo que hace con WatchSoMuch).

Botón **«Guardar y probar»**: guarda y lanza una búsqueda de prueba mostrando
resultados, errores, motor usado y un resumen del HTML recibido.

## Navegador (pestaña propia)

- **Motor**: `Automático` (CDP y, si falla, FlareSolverr), `Solo navegador`,
  `Solo FlareSolverr` o `Desactivado`.
- **Ejecutable**: vacío = detección automática (Chrome, Edge o Chromium; también
  rutas de Linux/macOS).
- **FlareSolverr**: URL del servicio (`http://localhost:8191`) si lo usas.
- **Tiempo máximo**, **cierre por inactividad**, **User-Agent** y **headless**.
- Botones **«Comprobar navegador»** y **«Probar URL»** (abre una URL y muestra
  motor, título, tamaño del HTML, cookies y URLs capturadas).

El navegador se arranca bajo demanda con un perfil temporal y se cierra solo
tras el tiempo de inactividad. No requiere instalar nada más que Chrome/Edge.

## Fuentes JSON y Torznab

- **JSON**: ruta al array de resultados (`data.items`, `$`) y mapa de campos
  (`title = name`, `magnet = magnet`, `seeds = seeders`…). Admite índices
  (`[0].name`) y filtrado local cuando la web devuelve un catálogo completo.
- **Torznab**: pega la URL de búsqueda de Jackett/Prowlarr sustituyendo la
  consulta por `{query}` (incluye la `apikey`). Se leen `title`, `link`,
  `enclosure`, `size`, `seeders`, `peers`, `pubDate`, categoría, póster e IMDb.

## Cliente de descargas

| Cliente | Puerto por defecto | Notas |
|---|---|---|
| Automático | — | Prueba qBittorrent, Transmission y Deluge en el host/puerto indicados |
| qBittorrent (WebUI) | 8080 | Requiere WebUI activada y usuario/contraseña |
| Transmission (RPC) | 9091 | Basic auth opcional |
| Deluge (Web) | 8112 | Contraseña del Web UI |
| Solo enlace magnet | — | Abre el `magnet:` con el programa del sistema |

Opciones: carpeta de descarga, categoría y etiquetas (qBittorrent) y añadir en
pausa. **«Probar conexión»** indica qué clientes responden.

Al pulsar **Descargar**:

1. Si el enlace se descubrió con el navegador y la web protege la descarga, se
   descarga el `.torrent` con las cookies de esa sesión y se envía como archivo.
2. Si el cliente responde, se añade el magnet o el `.torrent`.
3. Si no hay cliente disponible, la interfaz abre el magnet en el navegador.

## Uso del buscador

Caja de búsqueda + filtros (tipo, calidad, semillas mínimas, orden e idioma),
selector de fuentes, tarjetas con carátula y metadatos, ficha completa con
sinopsis, pares de datos, info-hash, **lista de archivos del .torrent** y
botones para enviar al cliente, abrir el magnet, descargar el `.torrent` o abrir
la ficha original. También hay **Enlace manual** para pegar magnets o URLs.

## API

Todo cuelga de `/Torrent/api` y requiere sesión de Jellyfin. (admin) = solo
administradores.

| Método | Ruta | Descripción |
|---|---|---|
| GET | `/config` | Fuentes y ajustes para la interfaz |
| POST | `/search` | Búsqueda agregada |
| POST | `/add` | Envía un magnet/.torrent al cliente (acepta `resultId` para usar la sesión del navegador) |
| GET | `/image?u=&r=` | Proxy de carátulas con caché |
| GET | `/torrent?u=&r=` | Descarga el .torrent a través del plugin |
| GET | `/files?u=&r=` | Info-hash, tamaño y archivos del .torrent |
| POST | `/source/test` (admin) | Búsqueda de prueba con diagnóstico y motor usado |
| POST | `/domain/resolve` (admin) | Fuerza la resolución del dominio |
| GET | `/browser/probe` (admin) | Estado del navegador/FlareSolverr |
| POST | `/browser/test` (admin) | Abre una URL con el navegador y devuelve el diagnóstico |
| GET | `/client/probe` (admin) | Estado de los clientes de descarga |
| GET | `/templates` (admin) | Plantillas incluidas |

## Cómo funciona el navegador

1. Se arranca Chrome/Edge con `--headless=new --remote-debugging-port=0` y un
   perfil temporal.
2. Se conecta por **CDP** (WebSocket): navega, espera `readyState`, espera el
   selector configurado, ejecuta tus scripts y lee `document.documentElement.outerHTML`.
3. Se **capturan las peticiones de red** (`Network.requestWillBeSent` y
   `Browser.downloadWillBegin`): así se obtienen los `.torrent` que genera la
   propia web (botones con proof-of-work, redirecciones, descargas).
4. Se guardan las **cookies** de la sesión por resultado para poder descargar
   después el `.torrent` protegido y enviarlo al cliente.
5. El navegador se cierra solo tras el tiempo de inactividad configurado.

No se resuelven captchas de imagen: si una web los exige, la fuente devolverá
el error correspondiente y podrás abrir la ficha a mano.

## Limitaciones

- Webs que exigen **login/VIP** para buscar o descargar necesitan tu cookie
  (campo *Cookie de la fuente*) o una fuente Torznab.
- **Captchas** de imagen y verificaciones interactivas no se resuelven.
- La contraseña del cliente y el token del bot de Discord se guardan en la
  configuración XML del servidor.
- El navegador consume RAM mientras está abierto; ajústalo o desactívalo si el
  servidor es pequeño.

## Aviso

El plugin es una herramienta genérica de búsqueda y envío de enlaces: no alberga
contenido ni conoce qué descargas. Eres responsable de usar solo fuentes y
contenido que puedas descargar legalmente en tu país.

## Licencia

GPL-2.0 — ver [LICENSE](LICENSE).
