# Lista kontrolna (ręczna)

## Powłoka i Ustawienia

1. Pierwsze uruchomienie (pusta baza): „Na żywo” pokazuje „Dodaj listę” i podpowiedź o EPG; przycisk otwiera Ustawienia z oknem dodawania. **[zweryfikowane przez agenta]** na opublikowanej wersji i świeżym folderze danych: przycisk otwiera Ustawienia i okno dodawania od razu, bez komunikatu o błędzie.
2. Dodaj listę z `samples/` (plik) i EPG z `samples/epg/pl.gz`: oba źródła mają datę odświeżenia, a wiersz EPG „Dopasowano X z Y kanałów” (Y to wszystkie kanały z list). **[zweryfikowane przez agenta]** (lista z pliku, 525 kanałów; EPG z pliku: „Dopasowano 525 z 525 kanałów”).
3. W wierszu nie widać adresu URL, nazwy pliku z identyfikatorem ani ścieżki do katalogu użytkownika. **[zweryfikowane przez agenta]** dla źródeł z adresu i z pliku o neutralnej nazwie; nazwy plików z identyfikatorem właściciela nie sprawdzano.
4. Podmień kopię pliku listy na tekst `<html>` i kliknij „Odśwież teraz”: pojawia się błąd po polsku, kanały zostają. **[zweryfikowane przez agenta]**
5. Dodaj adres `ftp://…`: komunikat pod polem znika po zmianie tekstu; poprawny `http(s)://` przechodzi. **[zweryfikowane przez agenta]**
6. „Usuń <nazwa>” pokazuje okno z nazwą źródła i „Anuluj”; po usunięciu reszta danych zostaje. **[zweryfikowane przez agenta]** (okno z nazwą i „Anuluj”; po usunięciu reszta zostaje).
7. Tab przechodzi przez wszystkie kontrolki z widocznym fokusem; przyciski ze strzałkami mają podpowiedź. **[do sprawdzenia przez właściciela]**
8. Motyw jasny i ciemny systemu; okno nie zmniejsza się poniżej 900×600. **[do sprawdzenia przez właściciela]** (motyw i minimalny rozmiar okna nie były sprawdzane).

## Na żywo

1. Po dodaniu listy i EPG widać trzy kolumny: kategorie z liczbami (zgodnymi z listą, „Wszystkie” i „Ulubione” na górze), kanały i obraz nad ramówką. Nic nie gra samo. **[zweryfikowane przez agenta]** (trzy kolumny, kategorie z liczbami, nic nie gra samo; zgodność liczb z listą nie była porównywana).
2. Wiersz kanału: logo na jasnej płytce albo inicjały, nazwa, bieżący program, cienki pasek postępu, „Dalej hh:mm Tytuł”; bez EPG napis „Brak EPG”. Długie nazwy kończą się wielokropkiem. **[zweryfikowane przez agenta]** (logo, program, pasek, „Dalej”, wielokropek); „Brak EPG” nie było widać.
3. Przewijanie „Wszystkie” (525 pozycji) jest płynne; program i pasek uzupełniają się dla widocznych wierszy, a po zatrzymaniu przewijania nic nie miga ani nie zmienia logo. **[do sprawdzenia przez właściciela]**
4. Kliknij 10 kanałów pod rząd: każdy startuje bez zawieszenia, grający ma znaczek odtwarzania, a ramówka otwiera się na bieżącym programie z nagłówkami „Dziś” i „Jutro”. **[zweryfikowane częściowo]** jeden kanał: startuje, znaczek odtwarzania, ramówka na bieżącym programie; 10 kanałów pod rząd **[do sprawdzenia przez właściciela]**.
5. Strzałki w liście kanałów tylko przesuwają fokus (widoczna ramka); Enter lub Spacja uruchamia kanał. **[do sprawdzenia przez właściciela]**
6. Wybranie programu w ramówce pokazuje opis, kategorię i obrazek; minione programy bez dostępnego archiwum są przygaszone; te z archiwum mają ikonę i można je odtworzyć (dwuklik lub „Oglądaj od początku”). **[zweryfikowane częściowo]** wybrany program pokazuje opis, kategorię i obrazek; reszta **[do sprawdzenia przez właściciela]**.
7. Gwiazdka pojawia się po najechaniu, na fokusie i zawsze, gdy kanał jest ulubiony; podpowiedź „Dodaj do ulubionych” / „Usuń z ulubionych”. Po „Odśwież teraz” w Ustawieniach gwiazdka zostaje. Menu kontekstowe wiersza (prawy przycisk lub Shift+F10) robi to samo. **[zweryfikowane częściowo]** dodanie do ulubionych przyciskiem, licznik „Ulubione”, gwiazdka zostaje po „Odśwież teraz”; najechanie, podpowiedź i menu kontekstowe **[do sprawdzenia przez właściciela]**.
8. „Ulubione” bez pozycji pokazuje podpowiedź, jak dodać kanał. **[do sprawdzenia przez właściciela]**
9. Wyszukiwarka (po 250 ms): „wiadomosci” znajduje „Wiadomości”; wynik z programu pokazuje „Znaleziono: …”; brak wyników daje „Brak wyników dla „…”” z przyciskiem „Wyczyść”. **[zweryfikowane częściowo]** „Znaleziono: …” przy wyniku z programu oraz „Brak wyników” z „Wyczyść”; kanału „Wiadomości” w tej liście nie ma, więc tego przykładu nie sprawdzono.
10. Przycisk z trzema kreskami zwija kategorie; poniżej ok. 1050 px szerokości strony zwijają się same, a kategorię wybiera się z listy rozwijanej (poniżej 900 px kolumna kanałów jest węższa). **[do sprawdzenia przez właściciela]**
11. Kanał z martwym adresem: po około pół minuty na obrazie „Nie udało się połączyć z kanałem…” i „Spróbuj ponownie”. **[do sprawdzenia przez właściciela]**
12. Przejście do Ustawień i z powrotem wznawia kanał, który grał (decyzja 17); usunięcie jego listy w Ustawieniach daje po powrocie pusty ekran bez dźwięku. **[do sprawdzenia przez właściciela]**
13. Motyw jasny: logo na płytce, przygaszone programy i pasek postępu są czytelne. **[do sprawdzenia przez właściciela]**

## Archiwum, sterowanie, pełny ekran (zadanie 12)

Sprawdzone przez agenta na prawdziwym strumieniu (kanał z zegarem na ekranie): 1, 2, 3, 5, 6, 8, 9 (bez kursora), 12.

1. Przeszły program z ikoną archiwum: dwukrotne kliknięcie lub „Oglądaj od początku” gra go od początku; na pasku „Archiwum: tytuł”, czas bieżący i całkowity.
2. Kliknięcie na osi czasu przewija w wybrane miejsce; zegar na ekranie kanału zgadza się z godziną programu plus pozycja (sprawdzone: 25:51 w programie od 00:00 pokazało 00:25).
3. „Wróć na żywo” (na pasku i na obrazie po końcu programu: „Program się skończył”) wraca do transmisji.
4. Program starszy niż archiwum: przygaszony, bez ikony, w panelu „Archiwum niedostępne dla tego programu”, przycisk nieaktywny. (Do sprawdzenia: wymaga programu starszego niż `timeshift`.)
5. Pasek sterowania pokazuje się po ruchu myszy, znika po 3 s; nie znika, gdy otwarte menu lub fokus klawiatury jest na kontrolce.
6. Skróty: Spacja (pauza), F, M, ↑ ↓ (kanał), Esc; w polu wyszukiwania nic nie jest przechwytywane; w liście kanałów strzałki ruszają listą; Spacja na przycisku naciska przycisk; w archiwum ← → przewijają o 10 s (← → nie sprawdzone).
7. Menu audio i napisów: aktywne tylko gdy jest z czego wybierać; wybór zmienia ścieżkę. (Do sprawdzenia przez właściciela: kanał z kilkoma ścieżkami.)
8. Pełny ekran (F, przycisk, dwuklik na obrazie): obraz wypełnia ekran bez kolumn i panelu; Esc wraca do poprzedniego okna.
9. W pełnym ekranie kursor znika razem z paskiem. (Do sprawdzenia przez właściciela: nie dało się zobaczyć kursora na zrzucie.)
10. Ekran nie gaśnie podczas odtwarzania i wraca do normy po pauzie. (Do sprawdzenia przez właściciela: wymaga czekania na wygaszenie.)
11. Po ponownym uruchomieniu wraca kategoria, kanał, wyciszenie, głośność i stan paska kategorii; nic nie gra samo. **[zweryfikowane częściowo]** po ponownym uruchomieniu opublikowanej aplikacji wrócił wybrany kanał i kategoria, nic nie grało samo; głośność, wyciszenie i pasek kategorii **[do sprawdzenia przez właściciela]**.
12. Seek nieobsługiwany przez serwer (ponowne otwarcie ze zmienionym startem): zweryfikowane tylko testami jednostkowymi.


## Odbiór opublikowanej wersji (zadanie 13)

Sprawdzone przez agenta na opublikowanym `M3uPlayer.App.exe`, na świeżym folderze danych:

1. Zamknięcie aplikacji w trakcie odświeżania: po ponownym uruchomieniu baza się otwiera, dane są nietknięte, a pozostały plik tymczasowy zostaje usunięty. **[zweryfikowane przez agenta]**
2. Brak sieci: źródło z nieosiągalnym adresem pokazuje błąd przy źródle; lista wczytana wcześniej zachowuje stare kanały i dostaje błąd, gdy serwer przestaje odpowiadać (odświeżanie trwa ok. 2,5 min z powodu ponowień). **[zweryfikowane przez agenta]**
3. Odświeżanie źródła EPG z prawdziwego adresu: drugie odświeżenie kończy się powodzeniem, zapisany jest ETag; aplikacja nie pokazuje komunikatu „nie zmieniono”, więc odpowiedzi 304 nie widać w interfejsie. **[zweryfikowane częściowo]**
4. Dźwięk, synchronizacja obrazu i dźwięku, motyw jasny, płynność przewijania, skróty klawiaturowe i fokus Tab. **[do sprawdzenia przez właściciela]**

## Wersje językowe

1. Ustawienia → Język: zmiana Polski ↔ English zamyka aplikację i uruchamia ją ponownie w wybranym języku; wraca ten sam kanał i kategoria.
2. Po angielsku: lista kanałów, ramówka (nazwy dni), archiwum (podpis i pasek), Ustawienia, dodawanie i usuwanie źródła, komunikat błędu odtwarzania. Żaden tekst nie zostaje po polsku i nigdzie nie widać nazwy klucza zamiast tekstu.
3. Pierwsze uruchomienie bez zapisanego języka: polski Windows daje polski, każdy inny angielski. **[do sprawdzenia przez właściciela]**


## Ustawienia

1. Zakładki Ogólne, Odtwarzanie, Źródła, Dane przełączają się; „Dodaj listę” z ekranu Na żywo otwiera zakładkę Źródła.
2. Motyw Jasny/Ciemny/Systemowy działa od razu, także w oknie dodawania źródła; pasek nad wideo zostaje ciemny.
3. Autostart + pełny ekran: po ponownym uruchomieniu ostatni kanał gra w pełnym ekranie; bez list nic się nie dzieje.
4. Bufor i dekodowanie sprzętowe działają od następnego kanału (log LibVLC: „no hw decoder modules matched” przy wyłączonym).
5. Skok przewijania i czas chowania paska na kanale z archiwum.
6. Domyślny interwał: nowa lista dostaje wybrany interwał.
7. Wyczyść pamięć logo: logo pobierają się ponownie. Wyczyść wszystkie dane: po potwierdzeniu aplikacja wraca pusta, `app.log` zostaje.
8. Po niemiecku w oknie 900 px etykiety mieszczą się lub zawijają. **[do sprawdzenia przez właściciela]**


## Kategorie

1. Prawy przycisk na kategorii: „Przypnij na górze” przenosi ją pod „Ulubione”; „Odepnij” wraca na miejsce z listy. Na „Wszystkie” i „Ulubione” menu się nie pokazuje.
2. Shift+F10 / klawisz Menu na kategorii w panelu i w rozwijanej liście wąskiego okna otwiera to samo menu.
3. „Ukryj” na otwartej kategorii z grającym kanałem: panel przechodzi na „Wszystkie”, kanał gra dalej, pojawia się informacja o Ustawieniach.
4. Kanały ukrytej kategorii nie są w „Wszystkie”, ale są w wyszukiwarce i w „Ulubione”.
5. Ustawienia → Kategorie: pola „Przypięta”/„Ukryta” wykluczają się; „Pokaż wszystkie kategorie” (z potwierdzeniem) przywraca ukryte; powrót na Na żywo pokazuje zmiany.
6. Ukrycie wszystkich kategorii: panel ma tylko „Wszystkie” (0) i „Ulubione”. **[do sprawdzenia przez właściciela]**
