# Deployment to Fontys ICT EduCloud

This is the runbook for putting the library system on a VM in
[Fontys ICT EduCloud](https://docs.educloud.fontysict.nl/) (Apache CloudStack). Follow
the steps in order; each one ends with a check so you know it worked before moving on.
Expect about 2–3 hours the first time.

## How it works

```
git push to main
   │  only the workflow of the service whose folder changed runs
   ▼
GitHub Actions (GitHub machine)        GitHub Actions (self-hosted runner on the VM)
  build image of that service    ──▶    pull that image from GHCR
  push to ghcr.io as :<commit sha>      restart only that container (--no-deps)

Internet / Fontys VPN ──▶ EduCloud firewall, port 80 ──▶ VM ──▶ api-gateway container
                                                                  ├─ book-service   ─┐
                                                                  ├─ member-service ─┼─ postgres
                                                                  └─ loan-service   ─┘
```

The files in the repo that make this work:

| File | What it does |
| --- | --- |
| `docker-compose.prod.yml` | Same services as `docker-compose.yml`, but with images from GHCR instead of `build:`, one tag variable per service, no default passwords, gateway on port 80 |
| `.github/workflows/book-service.yml` (and member, loan, api-gateway) | One pipeline per service, only started when that service's folder changes |
| `.github/workflows/build-and-deploy.yml` | The shared steps: build and push the image, then deploy it |
| `deploy/deploy-service.sh` | Runs on the VM: updates that service's tag in `.env`, pulls the image and restarts only that container |

Why a self-hosted runner: an EduCloud VM usually has a private (Fontys-internal) IP, so
GitHub can't SSH into it. The runner on the VM connects *out* to GitHub and asks for work,
which EduCloud does allow.

## What you need

- Your Fontys I-account and an authenticator app (EduCloud asks for 2FA).
- Admin rights on the GitHub repository (for adding a runner).
- An SSH client. Windows 11 has `ssh` built in.

---

## Step 1 — Log in to EduCloud

1. Go to <https://educloud.fontysict.nl>.
2. Login name: your **I-account**. Domain: your **semester code** (`S1`, `S2`, ...).
3. The first time, set up 2FA: pick Google Authenticator (or another TOTP app), scan the
   QR code and enter the code.

✅ You see the EduCloud dashboard.

## Step 2 — Create an SSH key and add it to EduCloud

On your own laptop (PowerShell):

```powershell
ssh-keygen -t ed25519 -f $HOME\.ssh\educloud
Get-Content $HOME\.ssh\educloud.pub
```

In EduCloud go to **Compute > SSH Key Pairs**, choose to add or register a key, give it
the name `educloud` and paste the contents of `educloud.pub`.

✅ The key pair `educloud` is in the list.

## Step 3 — Create an isolated network

The default shared network (`DHCP-Sx-Internet-Only`) only allows outgoing traffic. To
make the gateway reachable you need your own network with a firewall.

1. **Network > Guest Networks > Add Network**.
2. Tab **Isolated**. Name: `library-net`, zone: `Eindhoven-Zone`.
3. Network offering: **Offering for Isolated Network Outgoing Traffic Default Allowed**
   (the default one). Outgoing traffic is then allowed, which Docker needs to
   pull images and the runner needs to reach GitHub.
4. Leave everything else empty (gateway, netmask, DNS, VR address): EduCloud fills them in.
5. **OK**.

✅ `library-net` is in the Guest Networks list.

## Step 4 — Create the VM

1. **Compute > Instances > Add Instance**.
2. Template: an **Ubuntu Server** template (22.04 or 24.04).
3. Compute offering: **2 vCPU / 4 GB RAM** if available. 2 GB works but is tight with
   four .NET services and Postgres.
4. Root disk: 20 GB or more (images and the database).
5. Network: **`library-net`** (not the shared network).
6. SSH key pair: **`educloud`**.
7. Name: `library-vm`. **Launch virtual machine**.
8. If a password is shown after deploying, save it; it isn't shown again.
   bZhegTMK3

✅ `library-vm` has state **Running** and has an IP in `library-net`.

## Step 5 — Open ports 22 and 80

1. **Network > Guest Networks > library-net > Public IP addresses**. Click the IP that is
   marked as source NAT, or use **Acquire New IP** to get one.
2. **Write this IP down.** It's the address of your whole system. Below it's called `<VM-IP>`.
3. Tab **Firewall**, add two rules (leave the source CIDR empty or `0.0.0.0/0`):
   - TCP, start port `22` (SSH)
   - TCP, start port `80` (the API gateway)
4. Tab **Port Forwarding**, add two rules to `library-vm`:
   - public port `22` → private port `22`, TCP
   - public port `80` → private port `80`, TCP

Is `<VM-IP>` a **private** address (starts with `10.`, `172.16`–`172.31.` or `192.168.`)?
Then it's only reachable through the **Fontys OpenVPN**:

1. In EduCloud open **Downloads**, download **OpenVPN Connect** and the **configuration file**.
2. Install the client, **Upload File** → the configuration file → **Import** → **Connect**.

If a teacher needs to reach the demo without VPN, request a real public IPv4 address via
**TopDesk**. That isn't needed to finish today.

## Step 6 — Connect to the VM

```powershell
ssh -i $HOME\.ssh\educloud ubuntu@<VM-IP>
```

The user name is the one of the template (`ubuntu` for Ubuntu).

✅ You get a prompt like `ubuntu@library-vm:~$`.

If it times out: check the VPN (step 5), the firewall rule and port forward for port 22.
The **console** in the EduCloud portal (open the instance, then *View console*) works without
network access as a fallback.

## Step 7 — Install Docker on the VM

All the commands from here run **on the VM**. These are the steps from Docker's own
documentation for Ubuntu:

```bash
sudo apt-get update
sudo apt-get install -y ca-certificates curl
sudo install -m 0755 -d /etc/apt/keyrings
sudo curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
sudo chmod a+r /etc/apt/keyrings/docker.asc
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo "$VERSION_CODENAME") stable" | sudo tee /etc/apt/sources.list.d/docker.list > /dev/null
sudo apt-get update
sudo apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin

# Use docker without sudo (the runner needs this too)
sudo usermod -aG docker $USER
```

Log out (`exit`) and SSH in again so the group change is active.

✅ `docker run --rm hello-world` prints "Hello from Docker!" and
`docker compose version` shows a version.

## Step 8 — Create the deploy folder and `.env`

The `.env` file holds the real passwords and the API key. It only exists on the VM and
is never committed (Externalized Configuration).

```bash
sudo mkdir -p /opt/library
sudo chown $USER:$USER /opt/library
cd /opt/library

cat > .env <<EOF
# Images are pulled from here (GitHub user name in lowercase)
IMAGE_REGISTRY=ghcr.io/tiesvreeswijk

POSTGRES_USER=postgres
POSTGRES_PASSWORD=$(openssl rand -hex 16)
BOOK_DB_PASSWORD=$(openssl rand -hex 16)
MEMBER_DB_PASSWORD=$(openssl rand -hex 16)
LOAN_DB_PASSWORD=$(openssl rand -hex 16)

# Clients send this in the X-Api-Key header
GATEWAY_API_KEY=$(openssl rand -hex 16)

# The deploy script adds BOOK_SERVICE_TAG, MEMBER_SERVICE_TAG, ... below this line
EOF

chmod 600 .env
cat .env
```

Copy the `GATEWAY_API_KEY` somewhere safe (a password manager); you need it to call the API.

✅ `cat /opt/library/.env` shows the file with generated random values.

## Step 9 — Install the GitHub self-hosted runner

1. On GitHub open the repository → **Settings > Actions > Runners > New self-hosted runner**.
2. Choose **Linux**, architecture **x64**.
3. Run the commands from the **Download** section on the VM (they create `~/actions-runner`).
4. Run the `./config.sh --url ... --token ...` command from the **Configure** section.
   Answer the questions:
   - runner group: press Enter
   - name: `educloud`
   - **additional labels: `educloud`** (the workflows look for this label)
   - work folder: press Enter
5. Install it as a service, so it keeps running after you log out or the VM reboots:

   ```bash
   cd ~/actions-runner
   sudo ./bin/installdependencies.sh   # only needed if config.sh complained about missing libraries
   sudo ./svc.sh install $USER
   sudo ./svc.sh start
   ```

✅ On GitHub under **Settings > Actions > Runners** the runner `educloud` is **Idle** (green).

Note: the repository is public. The pipelines only start on a push to `main` or with the
*Run workflow* button. Both need write access to the repository, so pull requests from
strangers can't run code on your VM. Don't add a `pull_request` trigger to a workflow that
uses the self-hosted runner.

## Step 10 — Get the code onto `main`

The workflows only exist for GitHub once they're on `main`. Follow the branch flow:

1. PR `feature/api-gateway` → `develop`, merge.
2. PR `feature/deployment` → `develop`, merge.
3. PR `develop` → `main`, merge.

Merging into `main` changes all four service folders at once, so **all four pipelines
start**. That's the first deploy. Go to the **Actions** tab and watch them: each one has a
`build` job (GitHub machine) and then a `deploy` job (your VM). They deploy one after the
other because there's one runner.

✅ All four workflows are green.

If a workflow didn't start (for example because a folder didn't change), start it by hand:
**Actions** → choose the workflow → **Run workflow** → branch `main`.

## Step 11 — Check that it works

On the VM:

```bash
cd /opt/library
docker compose -f docker-compose.prod.yml ps
```

✅ Five containers with status `Up`: postgres, book-service, member-service, loan-service, api-gateway.

From your laptop (PowerShell, with the VPN on if needed):

```powershell
curl.exe http://<VM-IP>/health
curl.exe -H "X-Api-Key: <your GATEWAY_API_KEY>" http://<VM-IP>/api/books
curl.exe http://<VM-IP>/api/books
```

✅ `Healthy`, the five seed books, and a `401` for the request without a key.

**The deployment is done.** 🎉

## Step 12 — Show independent deployment (for your demo)

This is the proof that one service can be released without the rest.

1. On the VM, note the creation times: `docker ps --format "table {{.Names}}\t{{.CreatedAt}}"`.
2. Make a small change in **only** `services/BookService` (for example a comment or an
   extra field in the seed data), and take it to `main` via `develop`.
3. In **Actions**: only the **Book Service** workflow runs.
4. On the VM, run the `docker ps` command again: only `library-book-service-1` has a new
   creation time. `cat /opt/library/.env` shows the new `BOOK_SERVICE_TAG`, while the other
   tags stay the same.

Each service now has its own version (the commit SHA in its tag variable). That also
gives you a rollback: put an older SHA in that tag variable in `.env` and run
`docker compose -f docker-compose.prod.yml up -d --no-deps book-service`.

---

## Troubleshooting

| Problem | Cause and fix |
| --- | --- |
| `deploy` job stays on *Waiting for a runner* | The runner is offline or misses the label `educloud`. Check **Settings > Actions > Runners**; on the VM: `sudo ~/actions-runner/svc.sh status`. |
| `required variable ... is missing a value: set X in .env` | A line is missing in `/opt/library/.env` (step 8). |
| `permission denied ... docker.sock` in the deploy job | The runner user isn't in the `docker` group. Run `sudo usermod -aG docker $USER`, then `sudo ~/actions-runner/svc.sh stop` and `start`. |
| `denied` or `unauthorized` when pulling by hand on the VM | GHCR images are private by default. The workflow logs in by itself; by hand, make the package public (GitHub profile → **Packages** → package → **Package settings**) or `docker login ghcr.io` with a personal access token with `read:packages`. |
| Gateway gives 503 just after a deploy | That service is starting (migrations). Wait a few seconds. This is the same failure your circuit breaker will handle in sprint 5. |
| Services crash with a password error | The database was created with other passwords. The init script only runs on an empty volume. Only on a fresh setup: `docker compose -f docker-compose.prod.yml down -v` (this deletes the data) and deploy again. |
| `curl` to `<VM-IP>` times out | VPN not connected, or the firewall rule / port forward for port 80 is missing (step 5). |
| Logs of a service | `docker compose -f docker-compose.prod.yml logs -f book-service` in `/opt/library`. |
