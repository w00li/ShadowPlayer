# Lista kontrolna (ręczna)

## Powłoka i Ustawienia

1. Pierwsze uruchomienie (pusta baza): „Na żywo” pokazuje „Dodaj listę” i podpowiedź o EPG; przycisk otwiera Ustawienia z oknem dodawania.
2. Dodaj listę z `samples/` (plik) i EPG z `samples/epg/pl.gz`: oba źródła mają datę odświeżenia, a wiersz EPG „Dopasowano X z Y kanałów” (Y to wszystkie kanały z list).
3. W wierszu nie widać adresu URL, nazwy pliku z identyfikatorem ani ścieżki do katalogu użytkownika.
4. Podmień kopię pliku listy na tekst `<html>` i kliknij „Odśwież teraz”: pojawia się błąd po polsku, kanały zostają.
5. Dodaj adres `ftp://…`: komunikat pod polem znika po zmianie tekstu; poprawny `http(s)://` przechodzi.
6. „Usuń <nazwa>” pokazuje okno z nazwą źródła i „Anuluj”; po usunięciu reszta danych zostaje.
7. Tab przechodzi przez wszystkie kontrolki z widocznym fokusem; przyciski ze strzałkami mają podpowiedź.
8. Motyw jasny i ciemny systemu; okno nie zmniejsza się poniżej 900×600.

## Na żywo

1. Po dodaniu listy i EPG widać trzy kolumny: kategorie z liczbami (zgodnymi z listą, „Wszystkie” i „Ulubione” na górze), kanały i obraz nad ramówką. Nic nie gra samo.
2. Wiersz kanału: logo na jasnej płytce albo inicjały, nazwa, bieżący program, cienki pasek postępu, „Dalej hh:mm Tytuł”; bez EPG napis „Brak EPG”. Długie nazwy kończą się wielokropkiem.
3. Przewijanie „Wszystkie” (525 pozycji) jest płynne; program i pasek uzupełniają się dla widocznych wierszy, a po zatrzymaniu przewijania nic nie miga ani nie zmienia logo.
4. Kliknij 10 kanałów pod rząd: każdy startuje bez zawieszenia, grający ma znaczek odtwarzania, a ramówka otwiera się na bieżącym programie z nagłówkami „Dziś” i „Jutro”.
5. Strzałki w liście kanałów tylko przesuwają fokus (widoczna ramka); Enter lub Spacja uruchamia kanał.
6. Wybranie programu w ramówce pokazuje opis, kategorię i obrazek; minione programy są przygaszone i jeszcze nieklikalne w sensie archiwum.
7. Gwiazdka pojawia się po najechaniu, na fokusie i zawsze, gdy kanał jest ulubiony; podpowiedź „Dodaj do ulubionych” / „Usuń z ulubionych”. Po „Odśwież teraz” w Ustawieniach gwiazdka zostaje. Menu kontekstowe wiersza (prawy przycisk lub Shift+F10) robi to samo.
8. „Ulubione” bez pozycji pokazuje podpowiedź, jak dodać kanał.
9. Wyszukiwarka (po 250 ms): „wiadomosci” znajduje „Wiadomości”; wynik z programu pokazuje „Znaleziono: …”; brak wyników daje „Brak wyników dla „…”” z przyciskiem „Wyczyść”.
10. Przycisk z trzema kreskami zwija kategorie; poniżej ok. 900 px szerokości strony zwijają się same, a kategorię wybiera się z listy rozwijanej.
11. Kanał z martwym adresem: po około pół minuty na obrazie „Nie udało się połączyć z kanałem…” i „Spróbuj ponownie”.
12. Przejście do Ustawień i z powrotem zatrzymuje obraz; na ekranie jest nazwa kanału i „Oglądaj”.
13. Motyw jasny: logo na płytce, przygaszone programy i pasek postępu są czytelne.
