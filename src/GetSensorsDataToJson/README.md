# GetSensorsDataToJson residente para Zabbix

Reescritura en C# sobre **.NET 8 para Windows**. Mantiene una sola instancia de `LibreHardwareMonitor`, actualiza las temperaturas una vez por minuto y devuelve a Zabbix la última instantánea JSON desde memoria.

> **Por qué `net8.0-windows` y no .NET Standard:** .NET Standard es un contrato de API para bibliotecas, no un runtime para una aplicación de consola. Para modernizar una aplicación de .NET Framework y acceder a hardware/servicios de Windows, el destino correcto es .NET 8 para Windows.

## Qué resuelve

- `Computer.Open()` se ejecuta una vez en el proceso residente; el driver no se carga y descarga en cada consulta.
- Un único servidor se protege con un `Mutex` global.
- Los clientes se comunican por **Named Pipe**; una consulta caliente sólo lee un `string` JSON en memoria.
- La lectura del hardware ocurre cada 60 segundos, independientemente de la frecuencia de polling de Zabbix.
- Si una actualización falla, se conserva la última instantánea válida.
- El JSON mantiene el nombre histórico `SensorIdertifier` para no romper el preprocesamiento de Zabbix.
- El log no se escribe en stdout: queda en `%ProgramData%\GetSensorsDataToJson\GetSensorsDataToJson.log` y rota a 1 MiB.

## Arquitectura

```text
Zabbix UserParameter
        |
        | ejecuta EXE sin argumentos
        v
Cliente corto ---- Named Pipe ----> Proceso/servicio residente
  imprime JSON                         | caché JSON en memoria
  y termina                            | refresco cada 60 s
                                       v
                              LibreHardwareMonitor
                              Computer.Open() una vez
```

El mismo EXE tiene cuatro modos:

- Sin argumentos: cliente. Solicita `GET`, imprime una sola línea JSON y termina.
- `--server`: servidor residente. Es el modo usado por el servicio de Windows.
- `--status`: devuelve estado de la caché (`available`, `lastSuccessUtc`, `ageSeconds`, `lastError`, `refreshSeconds`).
- `--stop`: solicita un cierre ordenado de la instancia residente, imprime el resultado JSON y termina. No inicia una instancia si no existe ninguna.

Si no hay servidor, el cliente intenta iniciar una copia desacoplada con `--server`. Para producción se recomienda instalar el servicio de Windows, porque evita que las políticas de proceso/timeout de Zabbix puedan finalizar un proceso hijo.

## Compilar

Requisitos en la máquina de compilación:

- Windows 10/11 o Windows Server compatible.
- SDK de .NET 8.
- Acceso a NuGet durante el restore.

```powershell
cd .\GetSensorsDataToJson
.\publish.ps1
```

La salida autocontenida de 64 bits queda en:

```text
publish\win-x64\GetSensorsDataToJson.exe
```

No requiere instalar el runtime de .NET en el equipo monitoreado. Se usa `LibreHardwareMonitorLib` 0.9.6.

## Instalación recomendada como servicio

Abrir PowerShell **como administrador**:

```powershell
.\publish.ps1
.\install-service.ps1
```

El script:

1. copia la publicación a `C:\Program Files\Zabbix Agent 2\Scripts\GetSensorsDataToJson`;
2. crea o actualiza el servicio `GetSensorsDataToJson`;
3. configura inicio automático y recuperación ante fallos;
4. lo ejecuta como `LocalSystem`, igual que la instalación predeterminada de Zabbix Agent 2.

Si Zabbix Agent 2 usa una cuenta de servicio personalizada, configure el servicio `GetSensorsDataToJson` con la misma cuenta o defina ACL de Named Pipe adecuadas.

Pruebas:

```powershell
# Estado del servicio
Get-Service GetSensorsDataToJson

# Estado de la caché
& 'C:\Program Files\Zabbix Agent 2\Scripts\GetSensorsDataToJson\GetSensorsDataToJson.exe' --status

# Mismo JSON que recibe Zabbix
& 'C:\Program Files\Zabbix Agent 2\Scripts\GetSensorsDataToJson\GetSensorsDataToJson.exe'

# Cierre ordenado del proceso o servicio residente
& 'C:\Program Files\Zabbix Agent 2\Scripts\GetSensorsDataToJson\GetSensorsDataToJson.exe' --stop

# Volver a iniciar cuando está instalado como servicio
Start-Service GetSensorsDataToJson
```

Una detención correcta devuelve:

```json
{"stopping":true}
```

Si no existe una instancia residente, `--stop` devuelve `{"stopping":false,"reason":"not_running"}` y código de salida 1. Para detener el servicio creado por `install-service.ps1`, ejecute el comando desde una consola elevada o desde una cuenta con acceso a su Named Pipe. El cierre llama a `Computer.Close()`, libera el driver y finaliza el host de manera controlada.

Durante el arranque en frío, el primer `GET` espera como máximo 8 segundos por la primera lectura. Si el driver tarda más, devuelve `[]` antes de agotar el timeout; la actualización continúa en segundo plano. Con el servicio iniciado antes que Zabbix, las consultas normales ya son calientes.

## Zabbix Agent 2

Copiar `GetSensorsDataToJson.conf` a una ruta incluida por `zabbix_agent2.conf` y reiniciar el agente. La línea principal es:

```ini
UserParameter=GetTemperature,"C:\Program Files\Zabbix Agent 2\Scripts\GetSensorsDataToJson\GetSensorsDataToJson.exe"
```

Opcionalmente, importar el segundo UserParameter para observar la salud de la caché:

```ini
UserParameter=GetTemperature.status,"C:\Program Files\Zabbix Agent 2\Scripts\GetSensorsDataToJson\GetSensorsDataToJson.exe" --status
```

Conviene alertar si `available=false` o si `ageSeconds` supera 120–180 segundos.

## Configuración

Las variables se leen del entorno del proceso. Si se cambian a nivel de sistema, reiniciar el servicio.

| Variable | Valor predeterminado | Rango | Función |
|---|---:|---:|---|
| `GETTEMP_REFRESH_SECONDS` | 60 | 10–3600 | Intervalo de lectura del hardware. |
| `GETTEMP_CLIENT_TIMEOUT_MS` | 12000 | 1000–120000 | Límite total del cliente. |
| `GETTEMP_INITIAL_READ_WAIT_MS` | 8000 | 0–60000 | Espera máxima del primer `GET` por una instantánea real. |

Ejemplo:

```powershell
[Environment]::SetEnvironmentVariable('GETTEMP_REFRESH_SECONDS', '60', 'Machine')
Restart-Service GetSensorsDataToJson
```

## Driver, Event ID 7045 y Microsoft Defender

`LibreHardwareMonitor` necesita acceso de bajo nivel y abre su driver al ejecutar `Computer.Open()`. En el programa anterior, abrir y cerrar `Computer` en cada poll provocaba altas/bajas reiteradas del servicio de driver y eventos 7045. En esta versión, el objeto se mantiene abierto durante toda la vida del servicio y se cierra sólo al detenerlo; por lo tanto, la actividad deja de ocurrir en cada consulta.

Esto **no evita** un bloqueo de Defender contra un driver que la política corporativa considere vulnerable o PUA. Si Defender sigue bloqueándolo:

1. verificar que la compilación use el paquete oficial actualizado y conservar sus hashes;
2. revisar el evento/alerta exacto en Defender for Endpoint;
3. enviar el binario o hash al proceso corporativo de análisis y allowlisting;
4. aprobar sólo el hash/certificado necesario, con alcance mínimo y vencimiento/revisión;
5. no desactivar Defender ni crear una exclusión amplia para toda la carpeta de Zabbix.

Si la política prohíbe el driver incluso actualizado, la solución técnica correcta es usar una fuente de telemetría permitida (OEM/WMI/Redfish/ACPI) o desplegar un driver firmado y aprobado. Ningún cambio de C# puede eludir legítimamente un bloqueo del kernel.

## Comportamiento frente a fallos

- Error temporal de lectura: conserva el último JSON correcto y registra el error.
- Driver bloqueado desde el inicio: la caché queda en `[]`; `--status` muestra `available=false` y `lastError`.
- Servidor no disponible durante `GET` o `--status`: el cliente intenta iniciarlo dentro de un tiempo acotado; si falla, devuelve el valor de fallback y termina con código 1.
- Servidor no disponible durante `--stop`: no se crea una instancia nueva; devuelve `{"stopping":false,"reason":"not_running"}` y código 1.
- Consultas concurrentes: cada conexión tiene su propia instancia de Named Pipe y todas leen la misma caché inmutable.

## Archivos

- `Program.cs`: cliente, servidor, caché, lector de hardware y log.
- `GetSensorsDataToJson.csproj`: proyecto .NET 8 y dependencias NuGet.
- `publish.ps1`: publicación autocontenida `win-x64`.
- `install-service.ps1`: instalación/actualización del servicio.
- `GetSensorsDataToJson.conf`: UserParameters de Zabbix.
