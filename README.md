# M3U Player

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

Uruchom `artifacts/publish/M3uPlayer.App.exe`. Przy pierwszym uruchomieniu dodaj listę w Ustawieniach (adres albo plik), potem opcjonalnie źródło EPG.

## Gdzie są dane

`%LocalAppData%\M3uPlayer`: baza `m3u.db`, pamięć podręczna logo (`logos`), pliki tymczasowe (`tmp`) i dziennik `app.log`. Usunięcie folderu przywraca aplikację do stanu początkowego. Dziennik nie zawiera tokenów, ale komunikaty o błędach połączenia mogą zawierać nazwę serwera.

## Testy

```
dotnet test tests/M3uPlayer.Core.Tests
```

Testy używają wyłącznie zmyślonych danych. Ręczna lista kontrolna: `docs/manual-checklist.md`.

## Decyzje otwarte

Zob. `docs/superpowers/decisions`.
