# GloryHoleRefreshElevations

Обновляет отметки заданий на отверстия, округляет положение по выбранным настройкам и при необходимости перепривязывает болванки к уровням.

## Версии Revit

| Конфигурации | Целевая платформа |
| --- | --- |
| `R2019`–`R2024` | .NET Framework 4.8 (`net48`) |
| `R2025`, `R2026` | .NET 8 для Windows (`net8.0-windows`) |
| `R2027` | .NET 10 для Windows (`net10.0-windows`) |

Соответствие задаётся в `Directory.Build.props`. Сборка выполняется в Windows с .NET SDK 10 и средствами сборки .NET Framework 4.8. Revit API восстанавливается через NuGet для выбранной конфигурации.

## Сборка

Из корня репозитория, например для Revit 2023:

```powershell
dotnet build .\GloryHoleRefreshElevations\GloryHoleRefreshElevations.csproj -c R2023
```

Для всех поддерживаемых версий:

```powershell
foreach ($year in 2019..2027) {
    dotnet build .\GloryHoleRefreshElevations\GloryHoleRefreshElevations.csproj -c "R$year"
    if ($LASTEXITCODE -ne 0) { throw "Ошибка сборки Revit $year" }
}
```

Результат: `GloryHoleRefreshElevations/bin/R2019/` … `GloryHoleRefreshElevations/bin/R2027/`. Используйте DLL и необходимые зависимости из папки соответствующей версии. Бинарный комплект в корне репозитория предназначен для Revit 2023 и загрузки через RibbonCITRUS.

## Проверки

`GloryHoleRefreshElevations.Tests/GloryHoleRefreshElevations.Tests.csproj` — автоматические проверки xUnit на .NET 8. Нужен .NET 8 Runtime. Запуск без Revit:

```powershell
dotnet test .\GloryHoleRefreshElevations.Tests\GloryHoleRefreshElevations.Tests.csproj
```

Эти проверки не заменяют запуск команды в Revit на модели проекта.

## Структура

- `GloryHoleRefreshElevations/` — исходный код плагина и интерфейса.
- `data/` — название, ссылка на инструкцию и изображения команды для RibbonCITRUS.
- `Directory.Build.props` — конфигурации Revit и параметры сборки.
- `GloryHoleRefreshElevations.sln` — решение Visual Studio.
