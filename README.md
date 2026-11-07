# ShadowPlayer

Odtwarzacz telewizji z list M3U dla Windows 10/11 (WinUI 3, .NET 10, LibVLC): lista kanałów z kategoriami, ramówka z EPG (XMLTV), ulubione, wyszukiwarka i oglądanie archiwum (timeshift). Listy i EPG dodaje się z adresu internetowego albo z pliku.

## Budowanie

Wymagany .NET SDK 10. Z katalogu głównego:

```
dotnet build M3uPlayer.slnx -c Release -p:Platform=x64
```

Wersja samodzielna (bez instalowania .NET, z bibliotekami LibVLC):

```
dotnet publish src/M3uPlayer.App -c Release -r win-x64 -p:Platform=x64 --self-contained -o artifacts/publish
```

## Uruchomienie

Uruchom `artifacts/publish/ShadowPlayer.exe` albo zainstaluj aplikację (niżej). Przy pierwszym uruchomieniu dodaj listę w Ustawieniach (adres albo plik), potem opcjonalnie źródło EPG.

## Instalator

Każdy build `Release` tworzy `artifacts/installer/ShadowPlayer-Setup.exe` (build `Debug` go pomija). Wymaga Inno Setup 6 (`winget install JRSoftware.InnoSetup`); bez niego build kończy się ostrzeżeniem.

Instalator wymaga uprawnień administratora. Domyślnie instaluje aplikację w `C:\Program Files\ShadowPlayer`, a miejsce można zmienić przy pierwszej instalacji. Nowszy instalator uruchomiony na starszej wersji zamyka działającą aplikację i zastępuje poprzednią instalację w tym samym folderze; inne pliki w tym folderze zostają. Listy i ustawienia są w profilu użytkownika i zostają, także po odinstalowaniu. Numer wersji powstaje z daty i godziny buildu.

## Języki

Teksty są w `src/M3uPlayer.Core/Localization/Strings.resx` (polski, domyślny) i w `Strings.<kod>.resx` dla angielskiego, niemieckiego, hiszpańskiego, francuskiego, włoskiego i ukraińskiego. Tłumaczenia poza polskim i angielskim nie były sprawdzane przez osoby mówiące tymi językami. Język wybiera się w Ustawieniach; bez wyboru aplikacja bierze język Windows, a gdy go nie zna, angielski. Nowy język: skopiuj `Strings.en.resx` jako `Strings.<kod>.resx`, przetłumacz wartości i dopisz `("<kod>", "<nazwa w tym języku>")` w `Languages.All` (`Localization/Languages.cs`). Test `LocalizationTests` sprawdza, czy każdy język ma wszystkie teksty z tymi samymi `{0}`.

## Gdzie są dane

`%LocalAppData%\M3uPlayer` (nazwa sprzed zmiany na ShadowPlayer, zostawiona, żeby nie przenosić danych): baza `m3u.db`, pamięć podręczna logo (`logos`), pliki tymczasowe (`tmp`) i dziennik `app.log`. Usunięcie folderu przywraca aplikację do stanu początkowego. Dziennik nie zawiera tokenów, ale komunikaty o błędach połączenia mogą zawierać nazwę serwera.

## Testy

```
dotnet test tests/M3uPlayer.Core.Tests
```

Testy używają wyłącznie zmyślonych danych. Ręczna lista kontrolna: `docs/manual-checklist.md`.

## Ikona

Plik `src/M3uPlayer.App/Assets/ShadowPlayer.ico` powstaje ze skryptu `tools/make-icon.ps1`.

## Decyzje otwarte

Zob. `docs/superpowers/decisions`.
