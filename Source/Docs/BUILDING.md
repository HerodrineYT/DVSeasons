# Сборка DVSeasons

## Требования

- Windows и PowerShell 5.1+;
- 64-битный .NET SDK 8.0.421 или совместимый более новый SDK;
- установленная Derail Valley build 99;
- Unity Mod Manager в каталоге игры;
- для сборки Multiplayer-адаптера — установленный мод Multiplayer с `MultiplayerAPI.dll`.

Основная сборка не записывает файлы в игру. Игра используется только как источник compile-time DLL.

## Обычная сборка

```powershell
.\Tools\build.ps1
```

Скрипт ищет игру в Steam-каталогах на доступных файловых дисках. При нестандартной установке передайте путь:

```powershell
.\Tools\build.ps1 -DVInstallDir "D:\Games\Derail Valley"
```

Тесты можно запустить отдельно:

```powershell
& "C:\Program Files\dotnet\dotnet.exe" test .\DVSeasons.Tests\DVSeasons.Tests.csproj -c Release
```

Прямая сборка runtime-проекта:

```powershell
& "C:\Program Files\dotnet\dotnet.exe" build .\DVSeasons.Game\DVSeasons.Game.csproj -c Release -p:DVInstallDir="D:\Games\Derail Valley"
```

Готовая устанавливаемая папка создаётся в `artifacts/build/DVSeasons`.

Два отдельных релизных ZIP для Nexus Mods и GitHub Releases создаются так:

```powershell
.\Tools\package_combined.ps1 -Version 0.2.0
```

`DVSeasons-0.2.0-Nexus.zip` содержит только устанавливаемый runtime. `DVSeasons-0.2.0-GitHub.zip` дополнительно содержит каталог `Source` с C#-проектами, тестами, документацией, инструментами разработки, визуальными эталонами и исходным Unity-проектом. PDB, кэши, результаты сборки, вложенные архивы и дублирующий AssetBundle в релизы не включаются.

## AssetBundle

Обычная C#-сборка использует проверенный bundle из `Resources/Runtime/AssetBundles`. Для его пересборки требуется Unity 2019.4.40f1:

```powershell
.\Tools\build_assetbundle.ps1 -UnityEditor "C:\Program Files\Unity\Hub\Editor\2019.4.40f1\Editor\Unity.exe"
```

Unity собирает промежуточный deterministic LZ4 bundle из 127 PNG и трёх генерируемых `Texture2DArray`. Затем скрипт перепаковывает его в LZMA и атомарно заменяет runtime-копию. Для перепаковки нужен Python с пакетами из `Tools/requirements.txt`.
