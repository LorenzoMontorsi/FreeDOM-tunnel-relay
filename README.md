# FreeDOM

Tunnel self-hosted stile ngrok, in C# / .NET 10. Esponi un server locale (sito, API, SSH, giochi, qualsiasi TCP)
attraverso la tua VPS. **Nessun limite** di tempo, banda, numero di tunnel o connessioni: l'unico limite è la rete.

```
[Visitatore] ──HTTPS──> [Caddy :443] ──> [freedom-server :8080] <══TLS══ [freedom (client)] ──> localhost:3000
                                          │ :7835 canale di controllo
[Visitatore] ──TCP────> [freedom-server :20000-40000] ───────────────────┘
```

## Come funziona

- Il client apre una connessione **TLS persistente** verso la VPS (porta 7835): autenticazione con token, heartbeat,
  riconnessione automatica con backoff.
- Quando arriva un visitatore il server chiede al client di aprire una **connessione dati dedicata**; i byte vengono
  copiati in modo trasparente nei due sensi (WebSocket, SSE, upload enormi, tutto funziona).
- Tunnel `http`: smistati per sottodominio tramite l'header `Host`.
- Tunnel `tcp`: una porta pubblica dedicata sulla VPS.

## Componenti

| Binario | Cosa fa |
|---|---|
| `freedom-server` | il server da mettere sulla VPS |
| `FreeDOM` | client con interfaccia grafica (Windows, Linux, macOS) |
| `freedom-cli` | client da riga di comando, stessi tunnel e stesso file di configurazione |

## Build

Serve l'SDK di .NET 10.

```powershell
.\publish.ps1                          # win-x64, linux-x64, osx-arm64, osx-x64 -> dist/<piattaforma>/
.\publish.ps1 -Runtimes linux-arm64    # altre piattaforme
.\publish.ps1 -Runtimes win-x64 -GuiOnly
```

Binari singoli e autosufficienti (niente runtime da installare): ~11 MB server/CLI, ~21 MB la GUI.
Su macOS viene creato anche `FreeDOM.app`.

## Setup della VPS (Ubuntu/Debian)

1. **DNS**: record `A` per `tunnel.example.com` **e** `*.tunnel.example.com` → IP della VPS.
2. **Binario e servizio**:
   ```bash
   sudo useradd --system --home /opt/freedom freedom
   sudo mkdir -p /opt/freedom /etc/freedom
   sudo cp freedom-server /opt/freedom/ && sudo chmod +x /opt/freedom/freedom-server
   sudo chown -R freedom: /opt/freedom
   sudo cp deploy/server.env.example /etc/freedom/server.env   # modifica dominio e token (openssl rand -hex 32)
   sudo chmod 600 /etc/freedom/server.env
   sudo cp deploy/freedom-server.service /etc/systemd/system/
   sudo systemctl daemon-reload && sudo systemctl enable --now freedom-server
   journalctl -u freedom-server | grep fingerprint            # annota il fingerprint del certificato
   ```
3. **HTTPS con Caddy** (certificati Let's Encrypt automatici per ogni sottodominio):
   ```bash
   sudo apt install caddy
   sudo cp deploy/Caddyfile /etc/caddy/Caddyfile   # sostituisci tunnel.example.com
   sudo systemctl reload caddy
   ```
   > Il `keepalive off` nel Caddyfile è **obbligatorio**: senza, Caddy riuserebbe una connessione verso il server
   > per tunnel diversi.
4. **Firewall**: apri `80`, `443`, `7835` e il range TCP (`20000-40000`), es. `ufw allow 7835/tcp`.

Se sulla VPS c'è già nginx al posto di Caddy: basta un `server_name *.tunnel.example.com` con
`proxy_pass http://127.0.0.1:8080;` e `proxy_set_header Host $host;` (nginx non riusa le connessioni upstream di default).

## Client grafico (FreeDOM)

1. Inserisci indirizzo della VPS e token, aggiungi i tunnel (nome, tipo, porta locale, sottodominio o porta pubblica).
2. **Avvia**. Alla prima connessione ti mostra il fingerprint del server: confrontalo con il log del server e
   premi *Fidati e connetti* (viene memorizzato).
3. Gli URL pubblici compaiono sotto ogni tunnel con i pulsanti *Copia* / *Apri*.

- **La configurazione si salva da sola** a ogni modifica e viene ricaricata all'avvio:
  `%APPDATA%\FreeDOM\freedom.json` (Windows), `~/.config/FreeDOM/freedom.json` (Linux/macOS).
- *Connetti all'avvio*: apri il programma e i tunnel partono da soli.
- *Chiudendo la finestra resta nell'area di notifica*: continua a girare in background; dall'icona puoi riaprirlo,
  avviare/fermare o uscire. (Su Linux richiede un ambiente desktop con tray, su GNOME l'estensione AppIndicator.)
- Opzioni da riga di comando: `--minimized` (parte nascosto nella tray, utile all'avvio del PC) e
  `--config <file>` (profili multipli / versione portable su chiavetta).
- macOS: l'app non è firmata, al primo avvio fai tasto destro → Apri (oppure `xattr -cr FreeDOM.app`).
  Se copi i file da Windows, ricorda `chmod +x`.

## Client da riga di comando (freedom-cli)

```powershell
# veloce
freedom-cli http 3000 --server tunnel.example.com --token SEGRETO --fingerprint <SHA256> --subdomain blog
#   -> https://blog.tunnel.example.com
freedom-cli tcp 22 --server tunnel.example.com --token SEGRETO --fingerprint <SHA256> --remote-port 2222
#   -> ssh -p 2222 utente@tunnel.example.com

# con file di configurazione (più tunnel insieme, stesso formato della GUI)
freedom-cli init            # crea freedom.json di esempio
freedom-cli start
```

`freedom.json`:
```jsonc
{
  "server": "tunnel.example.com:7835",
  "token": "SEGRETO",
  "fingerprint": "6E692A40...",          // dal log del server
  "tunnels": [
    { "name": "web", "type": "http", "local": "3000", "subdomain": "blog" },
    { "name": "api", "type": "http", "local": "192.168.1.20:8080", "hostname": "api.miodominio.it" },
    { "name": "ssh", "type": "tcp",  "local": "22", "remotePort": 2222 }
  ]
}
```

- Senza `subdomain` ne viene generato uno casuale; senza `remotePort` una porta casuale nel range.
- `hostname` permette domini personalizzati (CNAME del tuo dominio → VPS).
- `"disabled": true` tiene un tunnel nella configurazione senza avviarlo.
- Se `freedom.json` esiste nella cartella corrente, `freedom-cli http/tcp` ne usa server, token e fingerprint.
- Variabili d'ambiente: `FREEDOM_SERVER`, `FREEDOM_TOKEN`.

## Opzioni del server

`freedom-server --help` per l'elenco completo. Le principali:

| Opzione | Default | Note |
|---|---|---|
| `--domain` | – | dominio base per i tunnel http (`FREEDOM_DOMAIN`) |
| `--token` | – | segreto condiviso (`FREEDOM_TOKEN`), oppure `--no-auth` |
| `--control-port` | 7835 | porta del canale di controllo |
| `--http-port` / `--http-bind` | 8080 / 0.0.0.0 | front HTTP; `127.0.0.1` dietro Caddy |
| `--ask-port` | off | endpoint per l'on-demand TLS di Caddy |
| `--tcp-ports` | 1024-65535 | range consentito per i tunnel TCP |
| `--tls` | auto | certificato self-signed autogenerato in `freedom-server.pfx`; `--tls-cert/--tls-key` per un PEM |

## Sicurezza

- Il canale client↔server è sempre TLS (salvo `--tls off`); il client verifica il server tramite fingerprint.
- Token confrontato in tempo costante.
- L'endpoint `ask` fa emettere certificati **solo** per i sottodomini con un tunnel attivo, quindi niente abuso dei
  rate limit di Let's Encrypt.
- Chi ha il token può aprire tunnel: non darlo in giro.

## Limiti noti

- Per i tunnel http dietro Caddy l'IP del visitatore arriva in `X-Forwarded-For`; sui tunnel TCP l'app vede l'IP del client locale.
- Non c'è riscrittura dell'header `Host` verso il server locale: dev server che verificano l'host (es. Vite) vanno configurati
  per accettare il dominio del tunnel.
