# Универсальный архив DVSeasons 0.2.0

Один ZIP одновременно является готовым пакетом Unity Mod Manager и архивом публикуемого исходного кода. Каталог `DVSeasons` можно передать UMM целиком; исходники находятся внутри `DVSeasons/Source` и не мешают загрузке мода.

Runtime находится в корне `DVSeasons`, а исходники и инструменты сборки — в `Source`. Внутри нет вложенных архивов или отдельных установщиков. Проверки Nexus Mods могут потребовать ручного рассмотрения DLL мода.

Сборка исходников:

```powershell
dotnet build .\DVSeasons.Game\DVSeasons.Game.csproj -c Release -p:DVInstallDir="D:\Games\Derail Valley"
```

Тесты:

```powershell
dotnet test .\DVSeasons.Tests\DVSeasons.Tests.csproj -c Release
```
