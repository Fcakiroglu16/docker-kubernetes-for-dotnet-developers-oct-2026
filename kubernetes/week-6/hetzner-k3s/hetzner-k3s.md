# hetzner-k3s ile Hetzner Cloud'da k3s Kurulumu

Bu rehber, [hetzner-k3s](https://github.com/vitobotta/hetzner-k3s) aracı ile Hetzner Cloud'da
**1 master + 2 worker** node'lu bir k3s cluster'ı kurmayı anlatır. Her adımda ne yapıldığını ve
neden yapıldığını açıklar.

> Test edildi: macOS (arm64), hetzner-k3s v2.11.0, k3s v1.37.2+k3s1, kubectl v1.36, helm v4.3.

## Neden hetzner-k3s?

k3s'i Hetzner'de elle kurmak için şunları tek tek yapmanız gerekir:

1. Private network, firewall ve SSH key oluşturmak.
2. Sunucuları doğru network ve firewall ile açmak.
3. Her sunucuya SSH ile girip k3s'i doğru parametrelerle kurmak.
4. Worker'ları master'a token ile bağlamak.
5. Hetzner Cloud Controller Manager (CCM) ve CSI driver'ı kurmak.

hetzner-k3s bu adımların hepsini tek bir YAML dosyasından yapar. Aynı dosya ile cluster'ı tekrar
kurabilir, büyütebilir veya silebilirsiniz. Yani cluster'ın tanımı kod olarak saklanır.

## Mimari

```text
                      İnternet
                         │
            ┌────────────┴────────────┐
            │  Hetzner Firewall       │  22 ve 6443: sadece sizin IP'niz
            │  (egitim)               │  30000-32767 (NodePort): herkes
            └────────────┬────────────┘
                         │ public IP'ler
     ┌───────────────────┼───────────────────┐
     │                   │                   │
┌────┴─────────┐  ┌──────┴───────┐  ┌────────┴─────┐
│ master1      │  │ worker1      │  │ worker2      │
│ cx23         │  │ cx23         │  │ cx23         │
│ k3s server   │  │ k3s agent    │  │ k3s agent    │
│ etcd         │  │ uygulamalar  │  │ uygulamalar  │
│ 10.0.0.2     │  │ 10.0.0.3     │  │ 10.0.0.4     │
└────┬─────────┘  └──────┬───────┘  └────────┬─────┘
     └───────────────────┼───────────────────┘
               Private network: egitim (10.0.0.0/16)
```

- **Master** (control plane): Kubernetes API'yi, scheduler'ı ve cluster durumunu tutan etcd'yi çalıştırır.
  Uygulama pod'ları buraya yerleşmez.
- **Worker'lar**: Uygulama pod'larını çalıştırır.
- **Private network**: Node'lar birbiriyle internete çıkmadan bu ağ üzerinden konuşur.
- **Firewall**: Dışarıdan gelen trafiği sınırlar. Private network trafiğine uygulanmaz.

## Maliyet

En ucuz uygun tip `cx23`'tür: 2 vCPU, 4 GB RAM, 40 GB disk. Daha küçük tipler (1 GB RAM) k3s ile
birlikte sistem pod'larını rahat çalıştıramaz.

| Kaynak | Adet | Aylık birim | Aylık toplam |
|---|---|---|---|
| `cx23` sunucu (fsn1) | 3 | 6,49 € | 19,47 € |
| Primary IPv4 | 3 | 0,60 € | 1,80 € |
| Private network, firewall, placement group, SSH key | - | ücretsiz | 0 € |
| **Toplam** | | | **~21,27 €** |

Hetzner saatlik ücretlendirir. Cluster'ı ders bitince silerseniz sadece kullandığınız saatleri ödersiniz.
Fiyatlar Kasım 2026 Hetzner API'sinden alınmıştır. Güncel fiyatlar için Hetzner Console'a bakın.

Bu kurulum ek ücretli kaynak açmaz:

- Tek master olduğu için Kubernetes API önünde load balancer yoktur.
- Cluster autoscaler kapalıdır. Yani cluster kendi kendine yeni sunucu açmaz.

Dikkat: Cluster içinde `type: LoadBalancer` servisi veya PVC oluşturursanız, Hetzner'de ücretli bir
Load Balancer veya Volume açılır. Bunlar `hetzner-k3s delete` ile silinmez, elle silmeniz gerekir.

## Gereksinimler

| Gereksinim | Neden gerekli |
|---|---|
| Hetzner Cloud hesabı ve proje | Kaynaklar bir projenin içinde oluşur |
| API token (Read & Write) | hetzner-k3s sunucu, network ve firewall oluşturmak için Hetzner API'sini çağırır |
| SSH key | hetzner-k3s node'lara SSH ile bağlanıp k3s'i kurar |
| kubectl | Kurulumdan sonra cluster'ı yönetmek için |
| hcloud CLI (opsiyonel) | Hetzner'de oluşan kaynakları kontrol etmek için (doğrulama adımları) |
| helm (opsiyonel) | İleride cluster'a paket kurmak için |

hcloud CLI'ı kurmak için:

```bash
brew install hcloud
```

---

## 1. hetzner-k3s'i kurun

```bash
brew install vitobotta/tap/hetzner_k3s
```

```bash
hetzner-k3s --version
```

hetzner-k3s sadece sizin makinenizde çalışan bir komut satırı aracıdır. Cluster'a hiçbir şey kurmaz,
kurulumu dışarıdan yönetir.

## 2. SSH key oluşturun

**Neden gerekli?** Hetzner API boş bir sunucu açabilir, ama sunucunun içinde komut çalıştıramaz.
hetzner-k3s her node'a SSH ile bağlanır ve şunları yapar:

- Master'da k3s'i server olarak kurar.
- Master'dan join token'ını ve kubeconfig'i alıp makinenize getirir.
- Worker'larda k3s'i agent olarak kurar ve token ile master'a bağlar.

SSH ileride de lazım olur: worker eklerken, `hetzner-k3s run` ile node'larda komut çalıştırırken ve
node'a girip log incelerken. Örneğin worker1'de `uptime` komutunu çalıştırmak için:

```bash
hetzner-k3s run --config cluster.yaml --instance egitim-pool-worker-worker1 --command "uptime"
```

Araç onay ister, `continue` yazıp Enter'a basın:

```text
Type 'continue' to execute this command on instance: continue
=== Instance: egitim-pool-worker-worker1 (2.31.133.62) ===
10:55:04 up 12:07,  1 user,  load average: 0.01, 0.41, 0.34
Command completed successfully
```

`--instance` verilmezse komut tüm node'larda çalışır.

SSH key ile açılan Hetzner sunucusunda root şifresi oluşturulmaz. Giriş sadece key ile yapılabilir.
Bu, şifre ile kaba kuvvet saldırılarını baştan engeller.

Daha önce oluşturduysanız bu adımı atlayın:

```bash
ssh-keygen -t ed25519 -f ~/.ssh/hetzner_k3s -N ""
```

- `-t ed25519`: Modern, kısa ve güvenli bir anahtar tipi.
- `-f ~/.ssh/hetzner_k3s`: Sadece bu cluster için ayrı bir key. Başka sunucularınızın key'i ile karışmaz.
- `-N ""`: Passphrase yok. hetzner-k3s'in key'i şifre sormadan kullanabilmesi için.

Passphrase kullanmak isterseniz config'te `use_agent: true` yapın ve key'i agent'a ekleyin:

```bash
ssh-add --apple-use-keychain ~/.ssh/hetzner_k3s
```

## 3. API token'ı ayarlayın

Hetzner Console'da şu yola gidin: Proje > Security > API Tokens > Generate API Token.
Yetki olarak **Read & Write** seçin. Read yetkisi ile sunucu oluşturulamaz.

Token bir kullanıcıya değil, sadece o projeye bağlıdır. Token ile başka projelerinize erişilemez.

Token'ı config dosyasına yazmayın, çünkü `cluster.yaml` git'e girer. Token'ı ortam değişkeni olarak
verin. hetzner-k3s önce `HCLOUD_TOKEN` değişkenine bakar.

```bash
export HCLOUD_TOKEN=<api-token>
```

hcloud CLI'a daha önce token verdiyseniz, aynı token'ı oradan okuyabilirsiniz:

```bash
export HCLOUD_TOKEN=$(sed -nE 's/.*token *= *"([^"]*)".*/\1/p' ~/.config/hcloud/cli.toml)
```

## 4. Kendi IP'nizi öğrenin

```bash
curl -s https://ifconfig.me
```

**Neden?** SSH (22) ve Kubernetes API (6443) portlarını sadece kendi IP'nize açacağız. İnternete açık
SSH ve API portları sürekli otomatik taramalara maruz kalır. Sadece kendi IP'nize açmak, bu
saldırıları firewall seviyesinde durdurur.

---

## 5. Config dosyası: cluster.yaml

Bu klasördeki [`cluster.yaml`](cluster.yaml) dosyası cluster'ın tamamını tanımlar. Aşağıda her bölüm
ayrı ayrı açıklanıyor. `allowed_networks` altındaki IP'yi kendi IP'nizle değiştirin.

### 5.1 Genel ayarlar

```yaml
cluster_name: egitim
kubeconfig_path: "./kubeconfig"
k3s_version: v1.37.2+k3s1
config_format_version: 1
```

| Ayar | Ne yapar |
|---|---|
| `cluster_name` | Hetzner'de oluşan tüm kaynakların adının ön ekidir: `egitim-master1` sunucusu, `egitim` network'ü, `egitim` firewall'u. Aynı projede birden fazla cluster olursa, kaynakları bu adla ayırt edersiniz. |
| `kubeconfig_path` | Kurulum sonunda kubectl'in kullanacağı dosyanın yolu. Komutu çalıştırdığınız klasöre göredir. |
| `k3s_version` | Kurulacak k3s sürümü. Sabit bir sürüm yazılır. Böylece aynı config her zaman aynı sürümü kurar. |
| `config_format_version` | Config şemasının sürümü. Araç bunu eski config'leri tanımak için kullanır. |

Kullanılabilir k3s sürümlerini görmek için:

```bash
hetzner-k3s releases
```

`-rc` ile biten sürümler test sürümleridir. Sonu `+k3s1` gibi biten kararlı bir sürüm seçin.

### 5.2 SSH ayarları

```yaml
networking:
  ssh:
    port: 22
    use_agent: false
    public_key_path: "~/.ssh/hetzner_k3s.pub"
    private_key_path: "~/.ssh/hetzner_k3s"
```

| Ayar | Ne yapar |
|---|---|
| `public_key_path` | Bu dosya Hetzner'e `egitim` adıyla yüklenir ve her sunucunun `authorized_keys` dosyasına eklenir. |
| `private_key_path` | hetzner-k3s node'lara bu anahtarla bağlanır. Bu dosya makinenizden hiç çıkmaz. |
| `use_agent` | `true` olursa anahtar dosyası yerine `ssh-agent` kullanılır. Passphrase'li anahtarlar için gereklidir. |
| `port` | SSH portu. Varsayılan 22. |

### 5.3 Erişim kısıtlaması

```yaml
  allowed_networks:
    ssh:
      - 78.162.65.17/32
    api:
      - 78.162.65.17/32
```

Bu listeler Hetzner firewall kurallarına dönüşür.

- `ssh`: 22 portuna kimlerin bağlanabileceği.
- `api`: 6443 portuna, yani Kubernetes API'ye kimlerin bağlanabileceği. `kubectl` bu porta gider.
- `/32`: Tek bir IP adresi demektir. Birden fazla IP veya bir ofis ağı için satır ekleyebilirsiniz.

hetzner-k3s, makinenizin IP'si bu listede yoksa kurulumu başlatmaz. Çünkü listede olmasaydınız
kurulumdan sonra cluster'a bağlanamazdınız.

### 5.4 Public network

```yaml
  public_network:
    ipv4: true
    ipv6: true
```

Her node'a bir public IP verilir.

- **IPv4 neden açık?** Node'ların internete çıkması gerekir: container imajı çekmek, k3s'i indirmek ve
  `apt` güncellemesi yapmak için. IPv4'ü kapatırsanız internet çıkışı için ayrı bir NAT gateway kurmanız
  gerekir. IPv4, IP başına aylık 0,60 € ücretlidir.
- **IPv6 neden açık?** Ücretsizdir. Bazı kaynaklara IPv6 ile erişmek gerekebilir.

### 5.5 Private network

```yaml
  private_network:
    enabled: true
    subnet: 10.0.0.0/16
```

- Node'lar birbirleriyle `10.0.x.x` IP'leri üzerinden konuşur: pod trafiği, etcd ve kubelet.
- Bu trafik internete çıkmaz. Hem daha hızlıdır hem de dışarıdan görünmez.
- Private network trafiği ücretsizdir ve aylık trafik kotasından düşmez.
- `/16` aralığı 65.536 adres içerir. Node sayısı için fazlasıyla yeterlidir.

Hetzner private network'leri en fazla 100 node destekler. Daha büyük cluster'lar için farklı bir yapı
gerekir. Bu eğitim için sınır sorun değil.

### 5.6 CNI (pod ağı)

```yaml
  cni:
    enabled: true
    encryption: false
    mode: flannel
```

CNI, farklı node'lardaki pod'ların birbiriyle konuşmasını sağlar. Her pod'a bir IP verir
(`10.244.x.x`) ve node'lar arasında bir overlay ağ kurar.

- `mode: flannel`: k3s'in varsayılan CNI'ıdır. Hafiftir ve ek ayar istemez. Alternatif olarak
  `cilium` seçilebilir. Cilium daha fazla özellik sunar (network policy, gözlemleme), ama daha fazla
  kaynak kullanır.
- `encryption: false`: Pod trafiği zaten izole private network'ten akar. Şifreleme (WireGuard) her
  pakete ek CPU yükü getirir. Private network yoksa veya hassas veri taşıyorsanız `true` yapın.

### 5.7 Datastore

```yaml
datastore:
  mode: etcd
```

Kubernetes, cluster'ın tüm durumunu (pod'lar, servisler, secret'lar) bir veritabanında tutar.

- `etcd`: Master'ın içinde gömülü çalışır. Ek sunucu veya ücret gerektirmez.
- Neden etcd? İleride master sayısını 3'e çıkarıp HA cluster'a geçmenizi sağlar. Diğer seçenek
  `external` ise, harici bir PostgreSQL veya MySQL ister.

Tek master'da etcd yedeği almak önemlidir. Master'ın diski kaybolursa cluster durumu da kaybolur.
Yedek için `datastore.etcd` altındaki snapshot ve S3 ayarlarına bakın.

### 5.8 Master'a iş yükü

```yaml
schedule_workloads_on_masters: false
```

`false` olunca master'a `CriticalAddonsOnly=true:NoExecute` taint'i eklenir. Bu taint'e tolerance'ı
olmayan pod'lar master'a yerleşmez.

**Neden?** Master'ın 4 GB RAM'i API server ve etcd'ye kalır. Bir uygulama aşırı bellek kullansa bile
control plane etkilenmez. Küçük bir dev cluster'da worker'ları kapatıp sadece master kullanmak
isterseniz `true` yapabilirsiniz.

### 5.9 Master pool

```yaml
masters_pool:
  instance_type: cx23
  instance_count: 1
  locations:
    - fsn1
```

| Ayar | Ne yapar |
|---|---|
| `instance_type` | Sunucu tipi. Tipleri `hcloud server-type list` ile görebilirsiniz. |
| `instance_count` | Master sayısı. |
| `locations` | Master'ların açılacağı lokasyon(lar). |

**Tek master'ın anlamı:** Cluster HA değildir. Master çökerse çalışan pod'lar çalışmaya devam eder,
ama yeni pod oluşturulamaz, `kubectl` çalışmaz ve çöken pod yeniden başlatılmaz. Eğitim ve test için
yeterlidir. Production için 3 master kullanın.

**Neden 3, neden 2 değil?** etcd çoğunluk (quorum) ile karar verir. 2 master'da biri çökerse kalan tek
master çoğunluğu oluşturamaz, yani 2 master 1 master'dan daha dayanıklı değildir. 3 master'da bir
master çökse bile cluster çalışmaya devam eder. Bu yüzden master sayısı hep tek sayı olur.

**Neden fsn1?** fsn1 (Falkenstein), nbg1 (Nürnberg) ve hel1 (Helsinki) aynı fiyattadır. Önemli olan,
tüm node'ların aynı lokasyonda ve aynı network zone'da (`eu-central`) olmasıdır.

### 5.10 Worker pool

```yaml
worker_node_pools:
  - name: worker
    instance_type: cx23
    instance_count: 2
    location: fsn1
```

- Bir pool, aynı tip ve lokasyondaki worker'lardan oluşan bir gruptur. Farklı ihtiyaçlar için birden
  fazla pool tanımlanabilir. Örneğin genel işler için küçük, veritabanı için büyük sunucular.
- Node adları `<cluster_name>-pool-<pool adı>-worker<sıra>` şeklindedir: `egitim-pool-worker-worker1`.
- Worker sayısını artırmak için `instance_count` değerini değiştirip `create` komutunu tekrar çalıştırın.
  Ayrıntılar için [8. Yeni worker node ekleme](#8-yeni-worker-node-ekleme) bölümüne bakın.
- Worker'lar master ile aynı lokasyondadır. Node'lar arası gecikme düşük olur.

Pool'lara `labels` ve `taints` eklenebilir. Bu, week-5'teki NodeSelector ve NodeAffinity konularıyla
birlikte kullanılabilir.

### 5.11 Eklentiler (addons)

```yaml
addons:
  cluster_autoscaler:
    enabled: false
```

hetzner-k3s bazı eklentileri varsayılan olarak kurar. Config'te yazmadığınız eklentiler varsayılan
değerinde kalır.

| Eklenti | Varsayılan | Ne yapar |
|---|---|---|
| `cloud_controller_manager` | açık | Kubernetes'i Hetzner'e bağlar. `type: LoadBalancer` servisi açınca Hetzner Load Balancer oluşturur. Node'lara lokasyon ve IP bilgisini ekler. |
| `csi_driver` | açık | PVC açınca Hetzner Volume oluşturur ve pod'un çalıştığı node'a bağlar. Pod başka node'a taşınsa bile veri kalır. |
| `system_upgrade_controller` | açık | k3s sürümünü node'ları sırayla yükselterek günceller. SSH gerektirmez. |
| `cluster_autoscaler` | açık | Pod'lar sığmazsa yeni sunucu açar, boşalınca siler. Sadece autoscaling pool'larında çalışır. |
| `traefik` | kapalı | Ingress controller. |
| `servicelb` | kapalı | k3s'in kendi LoadBalancer'ı. CCM bu işi Hetzner LB ile yaptığı için kapalıdır. |
| `metrics_server` | kapalı | `kubectl top` ve HPA için CPU ve bellek ölçümleri. |

**Neden cluster_autoscaler kapalı?** Bu config'te autoscaling pool yok. Açık bırakılması gereksiz bir
pod çalıştırır. Ayrıca beklenmedik sunucu açılıp ek ücret çıkmasının önüne geçer.

**HPA kullanacaksanız:** week-4'teki HPA örnekleri metrics-server ister. Açmak için config'e şunu
ekleyip `create` komutunu tekrar çalıştırın:

```yaml
addons:
  metrics_server:
    enabled: true
```

### 5.12 Silme koruması

```yaml
protect_against_deletion: true
```

`hetzner-k3s delete` komutunu engeller. Yanlış config dosyası ile yanlış cluster'ın silinmesini önler.
Silmek istediğinizde önce `false` yapmanız gerekir. Ayrıntılar için
[10. Cluster'ı tamamen silme](#10-clusterı-tamamen-silme) bölümüne bakın.

### 5.13 API load balancer

```yaml
create_load_balancer_for_the_kubernetes_api: false
```

`true` olursa Kubernetes API'nin önüne bir Hetzner Load Balancer konur (aylık ~7,89 €). Bu, birden
fazla master olduğunda işe yarar: bir master çökse bile API diğer master'lardan cevap verir.

Tek master'da load balancer arkasında yine tek sunucu olur. Bu yüzden hiçbir fayda sağlamaz, sadece
ücret çıkarır. Bir dezavantajı daha var: Hetzner firewall'u load balancer'a IP kısıtlaması
uygulayamaz, yani `allowed_networks.api` kuralı etkisiz kalır.

---

## 6. Cluster'ı oluşturun

```bash
cd kubernetes/week-6/hetzner-k3s
```

```bash
hetzner-k3s create --config cluster.yaml | tee create.log
```

`tee create.log`, çıktıyı hem ekranda gösterir hem de dosyaya yazar. Hata olursa log'u sonradan
inceleyebilirsiniz.

Kurulum yaklaşık 3-5 dakika sürer. Araç sırayla şunları yapar:

| Adım | Ne yapar | Neden |
|---|---|---|
| 1 | SSH public key'i Hetzner'e yükler | Sunucular bu key ile açılır, root şifresi oluşmaz |
| 2 | `egitim` private network'ünü oluşturur | Node'lar arası trafik için |
| 3 | `egitim` firewall'unu oluşturur | Dışarıdan erişimi sınırlamak için |
| 4 | `spread` tipi placement group'lar oluşturur | Node'lar farklı fiziksel host'lara dağılır. Bir host arızalanırsa tüm node'lar birden gitmez. |
| 5 | 3 sunucuyu açar | Ubuntu 24.04, network ve firewall bağlı olarak |
| 6 | Master'a SSH ile bağlanıp k3s server kurar | Control plane ve etcd'yi başlatmak için |
| 7 | Master'dan token ve kubeconfig'i alır | Worker'ları bağlamak ve sizin kubectl kullanmanız için |
| 8 | Worker'lara SSH ile bağlanıp k3s agent kurar | Token ile master'a bağlanırlar |
| 9 | CCM, CSI driver ve System Upgrade Controller'ı kurar | Hetzner entegrasyonu için |
| 10 | `kubeconfig` dosyasını bu klasöre yazar | kubectl ile bağlanmak için |

Log'daki `[Instance ...] [INFO] Installing k3s...` satırları, aracın SSH ile node'larda çalıştırdığı
komutların çıktısıdır.

Başarılı kurulumun son satırları şuna benzer:

```text
[Instance egitim-pool-worker-worker1] k3s worker installation completed successfully
[Instance egitim-pool-worker-worker2] k3s worker installation completed successfully
[Control plane] Switched to context "egitim-master1".
```

**Komut idempotenttir.** Yani tekrar çalıştırmak güvenlidir. Var olan kaynakları atlar, eksik olanları
oluşturur, config'te değişen ayarları uygular. Kurulum yarıda kalırsa aynı komutu tekrar çalıştırın.

**kubeconfig dosyası cluster'a tam yetki verir.** Bu dosyaya sahip olan herkes cluster'ı yönetebilir.
`.gitignore`'a eklidir, git'e göndermeyin ve paylaşmayın.

---

## 7. Kurulumu doğrulayın

kubectl'e yeni cluster'ı kullanmasını söyleyin:

```bash
export KUBECONFIG=$PWD/kubeconfig
```

`KUBECONFIG` değişkeni sadece bu terminal için geçerlidir. `~/.kube/config` dosyanız değişmez. Yani
diğer cluster'larınız (kind, minikube) etkilenmez.

### 7.1 Node'lar hazır mı?

```bash
kubectl get nodes -o wide
```

```text
NAME                         STATUS   ROLES                VERSION        INTERNAL-IP   OS-IMAGE
egitim-master1               Ready    control-plane,etcd   v1.37.2+k3s1   10.0.0.2      Ubuntu 24.04.4 LTS
egitim-pool-worker-worker1   Ready    <none>               v1.37.2+k3s1   10.0.0.3      Ubuntu 24.04.4 LTS
egitim-pool-worker-worker2   Ready    <none>               v1.37.2+k3s1   10.0.0.4      Ubuntu 24.04.4 LTS
```

Kontrol edin:

- Üç node da `Ready` olmalı. `NotReady` ise node'un kubelet'i veya ağı çalışmıyordur.
- Master'ın rolü `control-plane,etcd` olmalı.
- `INTERNAL-IP` sütunu `10.0.0.x` olmalı. Bu, node'ların private network üzerinden konuştuğunu gösterir.

### 7.2 Sistem pod'ları çalışıyor mu?

```bash
kubectl get pods -A
```

```text
NAMESPACE        NAME                                     READY   STATUS
kube-system      coredns-...                              1/1     Running
kube-system      hcloud-cloud-controller-manager-...      1/1     Running
kube-system      hcloud-csi-controller-...                5/5     Running
kube-system      hcloud-csi-node-... (her node'da bir)    3/3     Running
kube-system      helm-install-gateway-api-crd-...         0/1     Completed
system-upgrade   system-upgrade-controller-...            1/1     Running
```

| Pod | Ne işe yarar |
|---|---|
| `coredns` | Cluster içi DNS. Pod'lar servislere adla (`my-service.default.svc`) ulaşır. |
| `hcloud-cloud-controller-manager` | Hetzner entegrasyonu: LoadBalancer servisleri ve node bilgileri. |
| `hcloud-csi-controller` | Volume oluşturma ve silme işlerini yönetir. |
| `hcloud-csi-node` | DaemonSet. Her node'da çalışır, volume'u node'a bağlar. |
| `helm-install-gateway-api-crd` | k3s'in Gateway API CRD'lerini kuran tek seferlik job. `Completed` olması normaldir. |
| `system-upgrade-controller` | k3s sürüm yükseltmelerini yönetir. |

İlk dakikada bazı pod'lar `ContainerCreating` durumunda olabilir. İmajlar indirilirken bu normaldir.
Bir dakika sonra tekrar bakın.

### 7.3 Master'da taint var mı?

```bash
kubectl describe node egitim-master1 | grep Taints
```

```text
Taints:             CriticalAddonsOnly=true:NoExecute
```

Bu taint, `schedule_workloads_on_masters: false` ayarının sonucudur. Uygulama pod'ları master'a yerleşmez.

### 7.4 Hetzner'de neler oluştu?

```bash
hcloud server list
hcloud network list
hcloud firewall list
hcloud placement-group list
```

Load balancer ve volume listesi boş olmalı. Yani ek ücretli kaynak yok:

```bash
hcloud load-balancer list
hcloud volume list
```

### 7.5 Pod'lar worker'lara yerleşiyor mu?

```bash
kubectl create deployment nginx --image=nginx --replicas=2
```

```bash
kubectl rollout status deploy/nginx
```

```bash
kubectl get pods -o wide
```

`NODE` sütununda sadece `egitim-pool-worker-worker1` ve `egitim-pool-worker-worker2` görünmelidir.
Bu, hem worker'ların çalıştığını hem de master taint'inin işe yaradığını gösterir. Ayrıca pod'ların
imaj çekebilmesi, internet çıkışının çalıştığını gösterir.

Test bitince silin:

```bash
kubectl delete deployment nginx
```

---

## 8. Yeni worker node ekleme

Cluster'ı büyütmek için yeni bir config veya komut gerekmez. Aynı `cluster.yaml` dosyasında worker
sayısını artırıp aynı `create` komutunu tekrar çalıştırırsınız. Araç config'teki hedef durumu Hetzner'deki
mevcut durumla karşılaştırır ve sadece eksik olanı oluşturur.

Bu örnekte 2 worker'lı cluster'a üçüncü worker eklenir. Aylık maliyet ~7,09 € artar
(6,49 € sunucu + 0,60 € IPv4).

### 8.1 Worker sayısını artırın

`cluster.yaml` içinde worker pool'un `instance_count` değerini `2`'den `3`'e çıkarın:

```yaml
worker_node_pools:
  - name: worker
    instance_type: cx23
    instance_count: 3
    location: fsn1
```

### 8.2 create komutunu tekrar çalıştırın

Token ve IP'nizin hâlâ geçerli olduğundan emin olun. IP'niz değiştiyse önce `allowed_networks`'ü
güncelleyin, yoksa araç başlamaz.

```bash
hetzner-k3s create --config cluster.yaml | tee scale-up.log
```

Bu işlem yaklaşık 1,5 dakika sürer. İlk kurulumdan kısadır, çünkü sadece bir sunucu açılır.

### 8.3 Araç ne yaptı?

Log'da var olan node'lar ile yeni node farklı davranır:

| Node | Log'da ne görünür | Anlamı |
|---|---|---|
| `egitim-master1` | `No change detected so skipping service start` | k3s yeniden başlatılmadı, control plane kesintisiz çalıştı |
| `worker1`, `worker2` | `No change detected so skipping service start` | Çalışan pod'lar etkilenmedi |
| `worker3` | `Creating instance egitim-pool-worker-worker3` | Yeni sunucu açıldı |
| `worker3` | `Private network IP: 10.0.0.5` | Sunucu private network'e bağlandı, sıradaki boş IP'yi aldı |
| `worker3` | `k3s worker installation completed successfully` | SSH ile k3s agent kuruldu ve master'a bağlandı |
| `Firewall` | `Updating firewall... firewall updated` | Firewall kuralları config ile eşitlendi |

Araç var olan node'lara da SSH ile bağlanır ve kurulum script'ini tekrar çalıştırır. Script k3s'in
zaten kurulu ve aynı ayarda olduğunu görür ve hiçbir şeyi değiştirmez. Komutun idempotent olması bu
demektir.

Araç `kubeconfig` dosyasını her çalıştırmada yeniden yazar. Dosyanın içeriği aynı kalır, terminalinizi
yeniden ayarlamanız gerekmez.

### 8.4 Yeni node'u doğrulayın

```bash
kubectl get nodes -o wide
```

```text
NAME                         STATUS   ROLES                AGE   INTERNAL-IP   EXTERNAL-IP
egitim-master1               Ready    control-plane,etcd   33m   10.0.0.2      2.31.133.62
egitim-pool-worker-worker1   Ready    <none>               32m   10.0.0.3      2.31.143.129
egitim-pool-worker-worker2   Ready    <none>               32m   10.0.0.4      2.31.72.191
egitim-pool-worker-worker3   Ready    <none>               18s   10.0.0.5      2.28.134.98
```

`AGE` sütununa bakın. Eski node'ların yaşı sıfırlanmadı, sadece `worker3` yeni.

Yeni node'da DaemonSet pod'ları otomatik başlar. Örneğin `hcloud-csi-node`, her node'da bir tane
çalışan bir DaemonSet'tir (week-5'teki DaemonSet konusu):

```bash
kubectl get pods -A -o wide --field-selector spec.nodeName=egitim-pool-worker-worker3
```

Pod ilk birkaç saniye `ContainerCreating` durumunda olabilir. İmaj indirildikten sonra `Running` olur.

CCM, yeni node'a Hetzner bilgilerini label olarak ekler:

```bash
kubectl get node egitim-pool-worker-worker3 --show-labels | tr ',' '\n' | grep -E "instance-type|topology"
```

```text
beta.kubernetes.io/instance-type=cx23
node.kubernetes.io/instance-type=cx23
topology.kubernetes.io/region=fsn1
topology.kubernetes.io/zone=fsn1-dc14
```

Bu label'lar NodeSelector ve NodeAffinity kurallarında kullanılabilir. Örneğin bir pod'u sadece belirli
bir sunucu tipinde çalıştırmak için.

Hetzner tarafında yeni sunucu, mevcut placement group'a ve firewall'a otomatik eklenir:

```bash
hcloud server list
hcloud placement-group list
```

```text
NAME                             SERVERS     TYPE
egitim-masters-pg1               1 server    spread
egitim-pool-worker-workers-pg1   3 servers   spread
```

Firewall sunuculara tek tek değil `cluster=egitim` label selector ile bağlı olduğu için, yeni sunucu
firewall'u ayrıca bir işlem yapmadan alır:

```bash
hcloud firewall describe egitim
```

### 8.5 Yeni node'a pod yerleşiyor mu?

```bash
kubectl create deployment nginx --image=nginx --replicas=3
```

```bash
kubectl rollout status deploy/nginx
```

```bash
kubectl get pods -o wide
```

```text
NAME                     READY   STATUS    IP           NODE
nginx-69b9cdbbdd-2xtzc   1/1     Running   10.244.5.3   egitim-pool-worker-worker3
nginx-69b9cdbbdd-8kjjg   1/1     Running   10.244.3.4   egitim-pool-worker-worker1
nginx-69b9cdbbdd-zfkc6   1/1     Running   10.244.1.6   egitim-pool-worker-worker2
```

Scheduler 3 replica'yı 3 worker'a dağıttı. `worker3`'teki pod'un `10.244.5.x` IP'si almasının
nedeni, flannel'in her node'a ayrı bir pod IP aralığı vermesidir.

```bash
kubectl delete deployment nginx
```

**Not:** Var olan pod'lar yeni node'a kendiliğinden taşınmaz. Kubernetes çalışan pod'ları yeniden
dağıtmaz. Yeni node sadece bundan sonra oluşturulan pod'ları alır. Var olan bir deployment'ı yeni node
dahil tüm worker'lara yeniden dağıtmak için pod'larını yeniden başlatın. Örneğin yukarıdaki `nginx`
deployment'ı silinmeden önce:

```bash
kubectl rollout restart deployment/nginx
```

```bash
kubectl get pods -l app=nginx -o wide
```

### 8.6 Farklı tipte worker eklemek

Bir pool'daki tüm node'lar aynı tiptedir. Farklı tipte worker istiyorsanız var olan pool'u
değiştirmek yerine yeni bir pool ekleyin:

```yaml
worker_node_pools:
  - name: worker
    instance_type: cx23
    instance_count: 2
    location: fsn1
  - name: big
    instance_type: cx33 # 4 vCPU, 8 GB RAM
    instance_count: 1
    location: fsn1
```

Yeni pool'un node'u `egitim-pool-big-worker1` adını alır.

---

## 9. Worker node'u tamamen silme

Bu bölüm, 8. bölümde eklenen `worker3`'ü cluster'dan ve Hetzner'den tamamen kaldırır. Cluster
tekrar 1 master + 2 worker'a döner.

**Önemli:** `instance_count` değerini azaltıp `create` komutunu çalıştırmak node'u **silmez**.
Bu test edildi: count `3`'ten `2`'ye düşürüldü, `create` hatasız bitti, ama `worker3` hem Kubernetes'te
hem Hetzner'de çalışmaya devam etti. Sunucu ücretlendirilmeye de devam eder. Silme işlemini elle
yapmanız gerekir.

Silme 4 adımdan oluşur. Sıra önemlidir:

| Adım | Komut | Neden |
|---|---|---|
| 1 | `kubectl drain egitim-pool-worker-worker3 --ignore-daemonsets --delete-emptydir-data` | Pod'ları diğer node'lara taşır. Uygulamalar kesintisiz çalışır. |
| 2 | `cluster.yaml` içinde `instance_count: 3` → `instance_count: 2` | Bir sonraki `create` çalıştırmasında node'un tekrar oluşturulmasını engeller |
| 3 | `kubectl delete node egitim-pool-worker-worker3` | Node'u Kubernetes'ten kaldırır |
| 4 | `hcloud server delete egitim-pool-worker-worker3` | Sunucuyu Hetzner'den siler, ücretlendirme durur |

Tüm adımlar sırayla (2. adım dosya düzenlemesidir, komut değildir):

```bash
kubectl drain egitim-pool-worker-worker3 --ignore-daemonsets --delete-emptydir-data

# cluster.yaml: worker pool'un instance_count değerini 3'ten 2'ye düşürün

kubectl delete node egitim-pool-worker-worker3
hcloud server delete egitim-pool-worker-worker3
```

Her adımın açıklaması ve çıktısı aşağıdaki alt bölümlerdedir.

### Hangi node'u silmelisiniz?

**Her zaman en yüksek numaralı worker'ı silin** (`worker3`). Araç node'ları `worker1`, `worker2` ...
`worker<count>` diye adlandırır. `instance_count: 2` iken `worker2`'yi silerseniz, araç bir sonraki
`create` çalıştırmasında `worker2`'yi eksik sayıp yeniden oluşturur. `worker3` de yerinde kalır.
Bu davranış adlandırma kuralından çıkarılmıştır, test edilmemiştir.

### 9.1 Node'daki pod'ları boşaltın (drain)

Silmeden önce node'da hangi pod'lar çalışıyor bakın:

```bash
kubectl get pods -A -o wide --field-selector spec.nodeName=egitim-pool-worker-worker3
```

Node'u boşaltın:

```bash
kubectl drain egitim-pool-worker-worker3 --ignore-daemonsets --delete-emptydir-data
```

```text
node/egitim-pool-worker-worker3 cordoned
Warning: ignoring DaemonSet-managed Pods: kube-system/hcloud-csi-node-2h49j
evicting pod default/nginx-69b9cdbbdd-szh58
pod/nginx-69b9cdbbdd-szh58 evicted
node/egitim-pool-worker-worker3 drained
```

`drain` iki iş yapar:

1. **cordon**: Node'u `SchedulingDisabled` yapar. Yeni pod bu node'a yerleşmez.
2. **evict**: Node'daki pod'ları kibarca sonlandırır. Pod bir Deployment'a aitse, Deployment eksik
   replica'yı başka bir node'da yeniden oluşturur.

Parametreler:

- `--ignore-daemonsets`: DaemonSet pod'ları (`hcloud-csi-node` gibi) her node'da bir tane çalışır.
  Başka node'a taşınamazlar. Bu parametre olmadan drain hata verir.
- `--delete-emptydir-data`: `emptyDir` volume kullanan pod'lar varsa, bu volume'lardaki veri silinir.
  Bu parametre olmadan drain bu pod'larda durur.

Pod'ların taşındığını kontrol edin:

```bash
kubectl get pods -o wide
```

```text
NAME                     READY   STATUS    NODE
nginx-69b9cdbbdd-2tv4g   1/1     Running   egitim-pool-worker-worker1
nginx-69b9cdbbdd-dtc26   1/1     Running   egitim-pool-worker-worker2
nginx-69b9cdbbdd-nkmkv   1/1     Running   egitim-pool-worker-worker1
```

`worker3`'teki pod silindi ve yerine `worker1`'de yeni bir pod (`nkmkv`) oluştu. Replica sayısı 3'te kaldı.

Node hâlâ cluster'dadır, sadece pod almaz:

```bash
kubectl get nodes
```

```text
NAME                         STATUS                     ROLES
egitim-pool-worker-worker3   Ready,SchedulingDisabled   <none>
```

Vazgeçerseniz node'u tekrar kullanıma açabilirsiniz:

```bash
kubectl uncordon egitim-pool-worker-worker3
```

**PVC kullanan pod'lar için not:** Hetzner Volume'ları sadece aynı lokasyondaki bir node'a bağlanabilir.
Tüm node'lar `fsn1`'de olduğu için pod başka bir worker'a taşındığında volume da onunla gider.

### 9.2 Config'i güncelleyin

`cluster.yaml` içinde `instance_count` değerini `3`'ten `2`'ye düşürün:

```yaml
worker_node_pools:
  - name: worker
    instance_type: cx23
    instance_count: 2
    location: fsn1
```

Bu adımı atlayıp count'u `3`'te bırakırsanız, bir sonraki `create` çalıştırmasında araç `worker3`'ü
eksik görür ve yeniden oluşturur.

### 9.3 Node'u Kubernetes'ten silin

```bash
kubectl delete node egitim-pool-worker-worker3
```

Bu komut node kaydını ve node'a bağlı DaemonSet pod'larını cluster'dan kaldırır. Sunucu hâlâ
Hetzner'de çalışır ve ücretlendirilir.

### 9.4 Sunucuyu Hetzner'den silin

Bu işlem geri alınamaz. Sunucunun diski de silinir.

```bash
hcloud server delete egitim-pool-worker-worker3
```

```text
Waiting for delete_server (server: 176257208) ... done
Server egitim-pool-worker-worker3 deleted
```

hcloud CLI yoksa sunucuyu Hetzner Console'dan da silebilirsiniz: Proje > Servers >
`egitim-pool-worker-worker3` > Delete.

### 9.5 Silmeyi doğrulayın

Kubernetes'te 3 node kalmalı:

```bash
kubectl get nodes
```

```text
NAME                         STATUS   ROLES
egitim-master1               Ready    control-plane,etcd
egitim-pool-worker-worker1   Ready    <none>
egitim-pool-worker-worker2   Ready    <none>
```

Hetzner'de 3 sunucu kalmalı ve placement group'ta 2 worker görünmeli:

```bash
hcloud server list
hcloud placement-group list
```

```text
NAME                             SERVERS     TYPE
egitim-masters-pg1               1 server    spread
egitim-pool-worker-workers-pg1   2 servers   spread
```

Sunucunun public IP'leri de silinmiş olmalı. IP'ler `AUTO DELETE: yes` ile oluşturulduğu için
sunucuyla birlikte silinir ve ücretlendirilmez:

```bash
hcloud primary-ip list
```

Listede sadece kalan 3 sunucunun IP'leri görünmelidir. `ASSIGNEE` sütunu boş olan bir IP kaldıysa,
o IP silinen sunucudan kalmıştır. `ID` sütunundaki değerle silin. Örneğin ID'si `162896624` ise:

```bash
hcloud primary-ip delete 162896624
```

Son kontrol olarak `create` komutunu tekrar çalıştırın. Config ile Hetzner artık eşit olduğu için
araç yeni sunucu açmaz. Log'da `Creating instance` satırı olmamalıdır:

```bash
hetzner-k3s create --config cluster.yaml
```

---

## Firewall kuralları

hetzner-k3s `egitim` adında bir firewall oluşturur. Firewall'u sunuculara tek tek değil, label
selector ile bağlar. Bu yüzden yeni eklenen node'lar firewall'u otomatik alır.

| Port | Kaynak | Amaç |
|---|---|---|
| TCP 22 | sizin IP'niz | SSH |
| TCP 6443 | sizin IP'niz | Kubernetes API (kubectl) |
| ICMP | herkes | ping ile erişim kontrolü |
| TCP/UDP 30000-32767 | herkes | NodePort servisleri |
| TCP/UDP tümü | `10.0.0.0/16` | Node'lar arası trafik |

Kuralları görmek için:

```bash
hcloud firewall describe egitim
```

**NodePort aralığı internete açıktır.** `type: NodePort` servisleri her node'un public IP'sinden
erişilebilir olur. Bu, servisleri test etmek için pratiktir. Kapatmak isterseniz config'e
`networking.node_port_firewall_enabled: false` ekleyin.

## IP'niz değişirse

Ev internetinin IP'si zamanla değişebilir. Değişirse `kubectl` ve `ssh` zaman aşımına uğrar. Çünkü
firewall yeni IP'nizi tanımaz.

1. Yeni IP'nizi öğrenin: `curl -s https://ifconfig.me`
2. `cluster.yaml` içindeki `allowed_networks` IP'lerini güncelleyin.
3. `create` komutunu tekrar çalıştırın. Sadece firewall güncellenir, sunuculara dokunulmaz.

```bash
hetzner-k3s create --config cluster.yaml
```

## Node'a SSH ile bağlanma

`hcloud server ip` komutu sunucunun public IP'sini verir. SSH komutuna doğrudan verilebilir:

```bash
ssh -i ~/.ssh/hetzner_k3s root@$(hcloud server ip egitim-master1)
```

```bash
ssh -i ~/.ssh/hetzner_k3s root@$(hcloud server ip egitim-pool-worker-worker1)
```

IP'leri listelemek için `hcloud server list` veya `kubectl get nodes -o wide` (`EXTERNAL-IP` sütunu)
komutlarını da kullanabilirsiniz.

k3s log'larını incelemek için, node'a girmeden SSH ile tek komut çalıştırın. Master'da servis adı
`k3s`, worker'larda `k3s-agent`'tır:

```bash
ssh -i ~/.ssh/hetzner_k3s root@$(hcloud server ip egitim-master1) journalctl -u k3s -n 50 --no-pager
```

```bash
ssh -i ~/.ssh/hetzner_k3s root@$(hcloud server ip egitim-pool-worker-worker1) journalctl -u k3s-agent -n 50 --no-pager
```

`-n 50` son 50 satırı gösterir. Canlı izlemek için `-n 50 --no-pager` yerine `-f` kullanın.

## 10. Cluster'ı tamamen silme

Silme işlemi geri alınamaz. Sunucular diskleriyle birlikte silinir, yani cluster'daki tüm veriler
(etcd, pod verileri, secret'lar) kaybolur. Saklamak istediğiniz bir şey varsa önce yedekleyin.

Ders bitince cluster'ı silmek, ücretlendirmeyi durdurmanın tek yoludur. Hetzner kapalı (`off`)
sunucuları da ücretlendirir. Cluster'ı aynı `cluster.yaml` ile birkaç dakikada yeniden kurabilirsiniz.

### 10.1 Silmeden önce: uygulama kaynaklarını temizleyin

`hetzner-k3s delete` sadece kendi oluşturduğu kaynakları siler. Cluster içinden oluşturulan
kaynakları silmez:

| Kaynak | Ne oluşturur | delete ile silinir mi? |
|---|---|---|
| Load Balancer | `type: LoadBalancer` servisi (CCM) | Hayır |
| Volume | PVC (CSI driver) | Hayır |

Bu kaynaklar cluster silindikten sonra da ücretlendirilmeye devam eder. En temiz yol, cluster
çalışırken onları Kubernetes'ten silmektir. Böylece CCM ve CSI, Hetzner tarafını da kendileri siler:

```bash
kubectl get svc -A --field-selector spec.type=LoadBalancer
kubectl get pvc -A
```

Listede kaynak varsa silin. Komutlara listedeki `NAME` ve `NAMESPACE` değerlerini yazın.
Örneğin [PrivateRegistry](../PrivateRegistry/private-registry.md) rehberindeki `week6-api` servisi için:

```bash
kubectl delete svc week6-api -n week6
```

Bir namespace'teki tüm PVC'leri silmek için:

```bash
kubectl delete pvc --all -n week6
```

Uygulamanın namespace'ini tamamen silmek, içindeki Service ve PVC'leri de siler:

```bash
kubectl delete namespace week6
```

### 10.2 Silme korumasını kaldırın

`cluster.yaml` içinde:

```yaml
protect_against_deletion: false
```

Bu ayar `true` iken `delete` komutu çalışmaz. Bu, yanlış klasörde veya yanlış config dosyasıyla
çalıştırılan bir `delete` komutunun başka bir cluster'ı silmesini önler.

### 10.3 delete komutunu çalıştırın

```bash
hetzner-k3s delete --config cluster.yaml | tee delete.log
```

Araç onay için cluster adını ister. `egitim` yazıp Enter'a basın:

```text
Please enter the cluster name to confirm that you want to delete it: egitim
```

Bu onay, `protect_against_deletion` ayarından sonra ikinci bir güvenlik kontrolüdür.

Araç kaynakları bağımlılık sırasına göre siler. Bir ağı, ona bağlı sunucular silinmeden silemezsiniz.
Bu yüzden önce sunucular, en son ağ ve firewall silinir:

```text
[Instance egitim-master1] Deleting instance egitim-master1...
[Instance egitim-pool-worker-worker1] Deleting instance egitim-pool-worker-worker1...
[Instance egitim-pool-worker-worker2] Deleting instance egitim-pool-worker-worker2...
[Placement group] ...placement group egitim-masters-pg1 deleted
[Placement group] ...placement group egitim-pool-worker-workers-pg1 deleted
[Private network] ...private network deleted
[Firewall] ...firewall label selectors removed.
[Firewall] ...firewall deleted.
[SSH key] ...SSH key deleted
```

| Sıra | Silinen | Neden bu sırada |
|---|---|---|
| 1 | 3 sunucu (paralel) | Diğer kaynaklar sunuculara bağlı. Sunucuların public IP'leri de onlarla birlikte silinir. |
| 2 | Placement group'lar | İçinde sunucu kalmadığı için silinebilir |
| 3 | Private network | Bağlı sunucu kalmadığı için silinebilir |
| 4 | Firewall | Önce label selector'ü kaldırılır, sonra firewall silinir |
| 5 | SSH key | Hetzner'deki kopyası silinir. `~/.ssh/hetzner_k3s` dosyalarınıza dokunulmaz. |

Araç bu klasördeki `kubeconfig` dosyasını da siler. Silinen bir cluster'ın kubeconfig'i işe yaramaz.

### 10.4 Tamamen silindiğini doğrulayın

Projede hiçbir kaynak kalmamalı. Her satır `0` göstermeli:

```bash
for r in server network firewall placement-group ssh-key primary-ip floating-ip load-balancer volume; do
  printf "%s: " $r
  hcloud $r list -o noheader | grep -c .
done
```

```text
server: 0
network: 0
firewall: 0
placement-group: 0
ssh-key: 0
primary-ip: 0
floating-ip: 0
load-balancer: 0
volume: 0
```

Silmeden önce ve sonra karşılaştırma:

| Kaynak | Silmeden önce | Silmeden sonra |
|---|---|---|
| server | 3 | 0 |
| primary-ip (IPv4 + IPv6) | 6 | 0 |
| placement-group | 2 | 0 |
| network | 1 | 0 |
| firewall | 1 | 0 |
| ssh-key | 1 | 0 |
| load-balancer | 0 | 0 |
| volume | 0 | 0 |

`load-balancer` veya `volume` satırında `0` dışında bir sayı görürseniz, bunlar 10.1'de silinmemiş
uygulama kaynaklarıdır. Elle silin:

```bash
hcloud load-balancer list
```

Listedeki `NAME` veya `ID` ile silin. Örneğin `week6-api` adlı LB için:

```bash
hcloud load-balancer delete week6-api
```

```bash
hcloud volume list
```

CSI driver'ın açtığı volume'ların adı `pvc-` ile başlar. Listedeki adla silin, örneğin:

```bash
hcloud volume delete pvc-3f1c2a9e-5b7d-4e0a-9c1f-2d8e6b4a7c10
```

Son olarak `kubeconfig` dosyasının silindiğini kontrol edin:

```bash
ls
```

### 10.5 Config'i eski haline getirin

`cluster.yaml` içinde silme korumasını tekrar açın:

```yaml
protect_against_deletion: true
```

Bir sonraki kurulumda cluster yine korumalı başlar. Cluster'ı yeniden kurmak için 6. adımdaki
`create` komutunu çalıştırmanız yeterlidir.
