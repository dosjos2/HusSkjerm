# Oppsett av Raspberry Pi for HusSkjerm

> **Status:** Koden er testet på PC (8 enhetstester, mock-modus og bilder), men **ikke på ekte skjerm eller ekte Aqara-konto**. Aqara-signering og skjermens init-sekvens er skrevet etter dokumentasjon/hukommelse. Feilsøkingstips står nederst.

## 1. Utstyr

- Raspberry Pi (Zero 2 W, 3 eller 4 – alle fungerer), microSD ≥ 8 GB, strøm
- Waveshare 7.5" E-Paper HAT (B), 800×480, svart/hvit/rød
- Wi-Fi

## 2. Koble til skjermen

HAT-en settes rett oppå 40-pinners GPIO-headeren (ingen ledninger). Er du usikker på kabelen (HAT med kabel i stedet for pinner), bruk:

| Skjerm | Pi (BCM) | Fysisk pin |
|---|---|---|
| VCC | 3,3 V | 1 |
| GND | GND | 6 |
| DIN (MOSI) | GPIO10 | 19 |
| CLK (SCLK) | GPIO11 | 23 |
| CS | GPIO8 (CE0) | 24 |
| DC | GPIO25 | 22 |
| RST | GPIO17 | 11 |
| BUSY | GPIO24 | 18 |
| PWR | GPIO18 | 12 |

Pinnene kan endres under `Display` i `appsettings.json`. Hvis HAT-en har en grensesnittbryter, velg 4-line SPI (posisjon «A»).

## 3. Installer Raspberry Pi OS

1. Bruk **Raspberry Pi Imager**. Velg *Raspberry Pi OS Lite (64-bit)* (32-bit på Zero W 1).
2. Trykk tannhjulet / «Rediger innstillinger»: hostname `husskjerm`, bruker + passord, Wi-Fi, land `NO`, aktiver SSH.
3. Skriv kortet, sett det i Pi-en og start. Vent 2–3 minutter.
4. Fra PC: `ssh <bruker>@husskjerm.local`

## 4. Aktiver SPI

```bash
sudo raspi-config nonint do_spi 0
sudo reboot
```

Etter omstart skal `ls /dev/spidev0.0` finnes.

## 5. Bygg og kopier programmet (fra Windows-PC)

```powershell
cd C:\Users\admjskot\booktok\HusSkjerm
.\deploy\publish.ps1                 # 64-bit (bruk -PiRid linux-arm for 32-bit)
scp -r dist\pi <bruker>@husskjerm.local:~/husskjerm-install
```

På Pi-en:

```bash
cd ~/husskjerm-install
chmod +x install.sh
sudo ./install.sh
```

Skriptet installerer avhengigheter (fonter, libgpiod), oppretter bruker, kopierer til `/opt/husskjerm`, og setter opp en systemd-tjeneste som **starter automatisk ved oppstart**.

Se logg: `journalctl -u husskjerm -f`

## 6. Aqara-oppsett

1. Gå til **Aqara Open Platform** (developer.aqara.com), logg inn med Aqara-kontoen, og opprett et prosjekt. Du får **AppId, KeyId og AppKey**.
2. Velg riktig region/domene (Europa: `open-ger.aqara.com`).
3. Rediger konfigurasjonen på Pi-en:

```bash
sudo nano /opt/husskjerm/appsettings.local.json
```

```json
{
  "Aqara": {
    "AppId": "...",
    "KeyId": "...",
    "AppKey": "...",
    "Account": "din-aqara-epost",
    "Domain": "open-ger.aqara.com"
  },
  "Weather": { "UserAgent": "HusSkjerm/1.0 (din@epost.no)" },
  "Panel": { "Password": "velg-et-passord" }
}
```

4. `sudo systemctl restart husskjerm`
5. Åpne panelet → fanen **Aqara**: 1) *Send kode*, 2) skriv inn koden du får på e-post/SMS og trykk *Logg inn*, 3) *Synkroniser enheter*.

`Panel:Password` er valgfritt, men anbefalt: panelet spør da om passord (brukernavn kan være hva som helst).

Temperatur leses fra ressurs `0.1.85` og luftfuktighet fra `0.2.85` (standard for Aqara temperatursensor); kan endres under `Aqara`.

## 7. Bruk kontrollpanelet

Åpne **http://husskjerm.local** fra mobil/PC på samme Wi-Fi.

- **Plantegning:** velg etasje, trykk *+ Rom*, dra for å flytte, dra i hjørnene for å endre størrelse (snapper til 10 px). Gi navn og huk av sensorer. Gjennomsnittet vises midt i rommet. *Lagre* til slutt.
- **Rotasjon:** sett sekunder per bilde, endre rekkefølge, skru av/på enkeltbilder, eget tidsvalg per bilde, og *Vis nå*.
- **Bilder:** last opp egne bilder (tilpasses 800×480 og dithres til svart/hvit/rød) og legg dem i rotasjonen.
- **Innstillinger:** sted/koordinater for yr.no-vær (f.eks. Oslo 59,91 / 10,75), valgfri utendørs Aqara-sensor og varselgrenser for rød temperatur.
- **Skjerm nå:** viser siste bilde sendt til skjermen.

## 8. Om e-ink og visningstid

Tre-fargers e-ink bruker 15–20 s på hver oppdatering. Waveshare anbefaler minst 180 s mellom oppdateringer og minst én full oppdatering i døgnet. Programmet:

- trekker oppdateringstiden fra ventetiden (10 s innstilt blir i praksis ca. 20 s mellom bildene),
- hopper over skjermoppdatering hvis bildet er identisk med forrige (og tvinger oppdatering minst hver time),
- putter skjermen i dvale (sleep) mellom oppdateringer.

**Anbefaling:** sett 60–300 s per bilde, eller bruk *Pause* når du vil la ett bilde stå. Standard 10 s er satt etter ønske, men gir flimring og slitasje.

## 9. Feilsøking

| Problem | Løsning |
|---|---|
| Panel utilgjengelig | `systemctl status husskjerm`; prøv IP-adressen i stedet for `.local` |
| Skjermen forblir blank / «busy timeout» | Sjekk at SPI er aktivert og HAT sitter riktig; sjekk `BusyPin` |
| Farger er byttet (svart ↔ hvit/rød) | Sett `Display:InvertBlack` og/eller `Display:InvertRed` til `true` |
| Manglende/rare tegn | `sudo apt install fonts-dejavu-core libfontconfig1` |
| Aqara-feil ved innlogging | Sjekk AppId/KeyId/AppKey/domene; se `/api/status` (fanen Innstillinger → Status). **Feil 106 = «Invalid sign»**: oftest feil AppKey/KeyId (byttet om, eller blanke tegn), eller feil region i `Aqara:Domain`. KeyId skal limes inn akkurat slik Aqara viser den. Signaturen er verifisert mot Aqara sitt eksempel. Andre koder: 108/2005 token utløpt (logg inn på nytt), 2002 feil AppId/AppKey, 2003/811/816 feil kode, 817 for mange koder |
| Ingen vær | Fyll inn breddegrad/lengdegrad og e-post i `Weather:UserAgent` |
| Test uten Aqara/vær | `HUSSKJERM_Aqara__Mock=true`, `HUSSKJERM_Weather__Mock=true` |
| Prøve uten skjerm | `Display:Driver` = `File` skriver `data/current.png` |

Oppdatering: kjør `publish.ps1`, `scp` og `sudo ./install.sh` på nytt. Konfig (`config.json`, bilder, `appsettings.local.json`) beholdes.
