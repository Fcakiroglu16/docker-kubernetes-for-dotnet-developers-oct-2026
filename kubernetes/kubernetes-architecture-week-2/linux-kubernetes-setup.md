# kind ile Kubernetes Kurulumu (Linux)

Bu rehber, Linux bir makinede **kind** (Kubernetes in Docker) ile **1 control-plane + 3 worker** node'lu bir
Kubernetes cluster'ı kurmayı anlatır. Her node aslında bir Docker container'ıdır, yani tek bir bilgisayarda
gerçek bir çok node'lu cluster çalıştırmış olursunuz.

İki yol var:

- **Hızlı yol:** `setup-kind.sh` script'ini çalıştırın. Tüm adımları yapar ve sonunda kurulumu doğrular.
- **Adım adım yol:** Aşağıdaki komutları tek tek çalıştırın ve her adımda ne olduğunu görün.

> Test edildi: Ubuntu 24.04 (arm64), Docker 29.9, kind v0.33.0, kubectl v1.37, Kubernetes v1.37.0.

## Gereksinimler

| Kaynak | En az |
|---|---|
| CPU | 2 çekirdek (4 önerilir) |
| RAM | 4 GB boş (8 GB önerilir) |
| Disk | 10 GB boş |
| Yetki | `sudo` kullanabilen bir kullanıcı |
| İnternet | Docker, kubectl, kind ve node imajını indirmek için |

Mimarinizi öğrenin. Sonraki adımlarda indirme adreslerinde kullanacaksınız:

```bash
uname -m
```

`x86_64` çıkıyorsa **amd64**, `aarch64` çıkıyorsa **arm64** kullanın.

---

## Hızlı yol: script ile kurulum

```bash
chmod +x setup-kind.sh
```

```bash
./setup-kind.sh
```

Script şunları yapar:

1. Docker yoksa kurar ve kullanıcınızı `docker` grubuna ekler.
2. inotify limitlerini yükseltir.
3. kubectl ve kind yoksa mimarinize uygun sürümü kurar.
4. `egitim` adında 1 control-plane + 3 worker'lı bir cluster oluşturur.
5. Kurulumu doğrular: 4 node Ready mi, sistem pod'ları çalışıyor mu, iş yükü worker'lara dağılıyor mu.

Sonunda yeşil renkte `Kurulum başarılı` yazısını görmelisiniz. Script'i tekrar çalıştırmak güvenlidir.
Kurulu olanları atlar ve sadece doğrulamayı yeniden yapar.

Ayarlar ortam değişkeni ile değiştirilebilir:

```bash
CLUSTER_NAME=deneme WORKERS=2 ./setup-kind.sh
```

Cluster'ı silmek için:

```bash
./setup-kind.sh --delete
```

---

## Adım adım kurulum

### 1. Docker'ı kurun

```bash
curl -fsSL https://get.docker.com | sudo sh
```

Docker'ı `sudo` olmadan kullanabilmek için kullanıcınızı `docker` grubuna ekleyin:

```bash
sudo usermod -aG docker $USER
```

Grup değişikliğinin geçerli olması için **oturumu kapatıp tekrar açın**. Hemen denemek isterseniz:

```bash
newgrp docker
```

Kontrol edin. `Hello from Docker!` yazısını görmelisiniz:

```bash
