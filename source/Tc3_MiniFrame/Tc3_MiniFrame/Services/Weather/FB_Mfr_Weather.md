# FB_Mfr_Weather Family

`FB_Mfr_Weather` introduces a service-backed weather contract for PlcHaGateway. The gateway writes values directly to primitive fields on the FB.

## Function Blocks

- `FB_Mfr_Weather` (abstract base)
- `FB_Mfr_WeatherNow` (current weather only)
- `FB_Mfr_WeatherForecast` (hourly/daily forecast slot)

`FB_Mfr_Weather` also contains `FltNtf : FB_Mfr_Notification` for runtime fetch/parse fault signaling.

## Mapping Attributes

Apply these attributes on the weather FB instance declaration:

- `{attribute 'PlcHa.Mapping' := 'weather.home'}`
- `{attribute 'PlcHa.Weather' := '<mode>'}`
- Optional `{attribute 'PlcHa.Name' := '...'}`

`PlcHa.Mapping` must be a fully-qualified Home Assistant weather entity.

## Mode Syntax

- `now` : current weather (`FB_Mfr_WeatherNow`)
- `Nh` : forecast around now + N hours (`FB_Mfr_WeatherForecast`)
- `Nd` : forecast around now + N days (`FB_Mfr_WeatherForecast`)

Examples:

```st
{attribute 'PlcHa.Mapping' := 'weather.home'}
{attribute 'PlcHa.Weather' := 'now'}
WthNow : FB_Mfr_WeatherNow;

{attribute 'PlcHa.Mapping' := 'weather.home'}
{attribute 'PlcHa.Weather' := '2h'}
Wth2h : FB_Mfr_WeatherForecast;

{attribute 'PlcHa.Mapping' := 'weather.home'}
{attribute 'PlcHa.Weather' := '12h'}
Wth12h : FB_Mfr_WeatherForecast;

{attribute 'PlcHa.Mapping' := 'weather.home'}
{attribute 'PlcHa.Weather' := '1d'}
Wth1d : FB_Mfr_WeatherForecast;

{attribute 'PlcHa.Mapping' := 'weather.home'}
{attribute 'PlcHa.Weather' := '3d'}
Wth3d : FB_Mfr_WeatherForecast;
```

## Field Contract

Common fields (`FB_Mfr_Weather`):

- `fTemp` (`REAL`)
- `eWthCond` (`E_Mfr_WthCond`)
- `fWndSpd` (`REAL`)
- `eWndBrng` (`E_Mfr_WndBrng`)
- `fPrec` (`REAL`)
- `bValid` (`BOOL`)

Current-only fields (`FB_Mfr_WeatherNow`):

- `fHmdt`, `fPres`, `fVis`, `fCld` (`REAL`)

Forecast-only fields (`FB_Mfr_WeatherForecast`):

- `fTempLw`, `fPrecProb` (`REAL`)
- `bDay` (`BOOL`)

## Error Handling

Initialization:

- Invalid mode syntax or invalid FB/mode combinations are rejected on gateway startup and logged.

Runtime:

- Weather service failures and parse failures are logged.
- On the first failure after a successful cycle, `FltNtf` is triggered.
