# Gateway API ile Alan Adından Servise Yönlendirme

Bu rehber, `order.kiteloomstudio.com` alan adına gelen istekleri **Gateway API** ile `week6-api`
servisine yönlendirmeyi anlatır. Gateway API controller'ı olarak k3s'in kendi **Traefik**'i kullanılır.

> Test edildi: k3s v1.37.2+k3s1, Traefik v3.7.13 (k3s chart 41.6.1), Gateway API CRD'leri (k3s gateway-api-crd 1.6.301).
> Ön koşul: [hetzner-k3s cluster'ı](../hetzner-k3s/hetzner-k3s.md) ve
> [PrivateRegistry](../PrivateRegistry/private-registry.md) rehberindeki `week6-api` deployment ve service'i.

## Akış

```text
 http://order.kiteloomstudio.com/
            │
            │ DNS (Cloudflare): order.kiteloomstudio.com → 2.31.140.127
            ▼
 ┌──────────────────────────┐
 │ Hetzner LB "traefik" :80 │   Traefik Service'i için CCM'in açtığı LB
 └────────────┬─────────────┘
              │ private network
              ▼
 ┌──────────────────────────┐   GatewayClass: traefik
 │ Traefik pod (:8000)      │   Gateway: week6-gateway (hostname: order.kiteloomstudio.com)
 └────────────┬─────────────┘   HTTPRoute: week6-api (path: / → week6-api:80)
              ▼
 ┌──────────────────────────┐
 │ Service week6-api :80    │
 │ 3 pod :8080              │
 └──────────────────────────┘
```

## Gateway API kaynakları

Gateway API üç katmandan oluşur. Her katmanı farklı bir rol yönetir:

| Kaynak | Kim yönetir | Ne tanımlar | Bu rehberde |
|---|---|---|---|
| `GatewayClass` | Altyapı sağlayıcı | Hangi controller'ın trafiği işleyeceği | `traefik` (Traefik chart'ı oluşturur) |
| `Gateway` | Platform / cluster yöneticisi | Hangi port, protokol ve alan adı dinlenecek | `week6-gateway`, [`2-gateway.yaml`](2-gateway.yaml) |
| `HTTPRoute` | Uygulama ekibi | Hangi isteğin hangi Service'e gideceği | `week6-api`, [`3-httproute.yaml`](3-httproute.yaml) |

Ingress'te bunların hepsi tek bir kaynaktaydı. Gateway API'de ayrı olduğu için uygulama ekibi kendi
HTTPRoute'unu yazar ama Gateway'i (port, sertifika) değiştiremez.

**Neden bir controller gerekiyor?** Gateway API kaynakları sadece tanımdır. k3s, Gateway API CRD'lerini
kurulumda zaten yükler (`helm-install-gateway-api-crd` job'u). Ama bu tanımları okuyup trafiği gerçekten
yönlendiren bir program olmadan hiçbir şey olmaz. Bu rehberde o program Traefik'tir.

## Dosyalar

```text
kubernetes/week-6/GatewayAPI/
├── 1-traefik-config.yaml   # Traefik'in Gateway API desteğini açar, LB ayarları
├── 2-gateway.yaml          # Gateway: order.kiteloomstudio.com, port 8000
├── 3-httproute.yaml        # HTTPRoute: / → week6-api:80
└── gateway-api.md
```

Komutlar bu klasörden ve admin kubeconfig ile çalıştırılır:

```bash
cd kubernetes/week-6/GatewayAPI
export KUBECONFIG=$PWD/../hetzner-k3s/kubeconfig
```

---

## 1. Traefik'in Gateway API desteğini açın

hetzner-k3s, k3s'in kendi Traefik'ini varsayılan olarak kapatır. Önce Traefik'in ayarlarını verin,
sonra Traefik'i açın. Sıra önemlidir: Traefik ilk açıldığında bu ayarlarla kurulur.

### 1.1 Traefik ayarları

```bash
kubectl apply -f 1-traefik-config.yaml
```

```text
helmchartconfig.helm.cattle.io/traefik created
```

k3s, Traefik'i bir Helm chart'ı olarak kurar. [`1-traefik-config.yaml`](1-traefik-config.yaml) bir
`HelmChartConfig`'tir ve bu chart'ın değerlerini (values) değiştirir:

```yaml
    providers:
      kubernetesGateway:
        enabled: true
    gateway:
      enabled: false
    service:
      annotations:
        load-balancer.hetzner.cloud/name: traefik
        load-balancer.hetzner.cloud/location: fsn1
        load-balancer.hetzner.cloud/type: lb11
        load-balancer.hetzner.cloud/use-private-ip: "true"
```

| Ayar | Neden |
|---|---|
| `providers.kubernetesGateway.enabled: true` | Traefik'in Gateway ve HTTPRoute kaynaklarını okumasını sağlar. Varsayılan `false`'tur. |
| `gateway.enabled: false` | Chart'ın varsayılan Gateway'ini oluşturmaz. Kendi Gateway'imizi yazıyoruz. |
| `service.annotations` | Traefik'in Service'i `type: LoadBalancer`'dır. Hetzner CCM bu annotation'larla bir LB açar. `location` olmadan LB oluşmaz. |

Bu ayarlar sonucunda chart, `traefik` adında bir GatewayClass oluşturur.

### 1.2 Traefik'i açın

`hetzner-k3s/cluster.yaml` içindeki `addons` bölümüne ekleyin:

```yaml
addons:
  traefik:
    enabled: true
```

Değişikliği uygulayın:

```bash
cd ../hetzner-k3s
hetzner-k3s create --config cluster.yaml
cd ../GatewayAPI
```

Araç k3s'i `--disable traefik` parametresi olmadan yeniden başlatır. Log'da şu satırları görürsünüz:

```text
[Instance egitim-master1] [INFO]  systemd: Starting k3s
[Instance egitim-master1] k3s installation completed successfully
```

Master'daki k3s yeniden başlarken Kubernetes API birkaç saniye cevap vermeyebilir. Worker'lardaki
pod'lar bu sürede çalışmaya devam eder.

### 1.3 Doğrulayın

Traefik pod'u çalışıyor mu:

```bash
kubectl -n kube-system rollout status deploy/traefik
```

```text
deployment "traefik" successfully rolled out
```

GatewayClass oluştu ve controller tarafından kabul edildi mi:

```bash
kubectl get gatewayclass
```

```text
NAME      CONTROLLER                      ACCEPTED   AGE
traefik   traefik.io/gateway-controller   True       18s
```

`ACCEPTED: True` olması, Traefik'in bu GatewayClass'ı sahiplendiğini gösterir.

Traefik'in LB'si oluştu mu. `EXTERNAL-IP` dolana kadar birkaç saniye bekleyin:

```bash
kubectl -n kube-system get svc traefik
```

```text
NAME      TYPE           CLUSTER-IP      EXTERNAL-IP                                   PORT(S)
traefik   LoadBalancer   10.43.245.233   10.0.0.7,2.31.140.127,2a01:4f8:c016:6cd3::1   80:31228/TCP,443:32151/TCP
```

```bash
hcloud load-balancer list
```

```text
NAME        HEALTH    IPV4           TYPE   LOCATION
week6-api   healthy   2.31.101.239   lb11   fsn1
traefik     healthy   2.31.140.127   lb11   fsn1
```

`2.31.140.127`, alan adının yönlendirileceği IP'dir.

Traefik'in Gateway desteğinin açık olduğunu başlatma parametrelerinden görebilirsiniz:

```bash
kubectl -n kube-system get deploy traefik -o jsonpath='{.spec.template.spec.containers[0].args}' | tr ',' '\n' | grep -iE "kubernetesgateway|entryPoints.web.address"
```

```text
"--entryPoints.web.address=:8000/tcp"
"--providers.kubernetesgateway"
```

`web` entrypoint'i container içinde `8000` portunu dinler. LB'nin `80` portu Service üzerinden bu
porta gelir. Gateway'in listener portu bu yüzden `8000`'dir.

---

## 2. Gateway

[`2-gateway.yaml`](2-gateway.yaml)

```yaml
apiVersion: gateway.networking.k8s.io/v1
kind: Gateway
metadata:
  name: week6-gateway
  namespace: week6
spec:
  gatewayClassName: traefik
  listeners:
    - name: http
      protocol: HTTP
      port: 8000
      hostname: order.kiteloomstudio.com
      allowedRoutes:
        namespaces:
          from: Same
```

| Alan | Ne yapar |
|---|---|
| `gatewayClassName: traefik` | Bu Gateway'i Traefik'in işleyeceğini söyler |
| `listeners[].protocol: HTTP` | Düz HTTP. HTTPS için sertifika gerekir, bkz. [HTTPS](#https) |
| `listeners[].port: 8000` | Traefik'in `web` entrypoint portu. Traefik'te listener portu, entrypoint portuyla eşleşmelidir. |
| `listeners[].hostname` | Sadece bu alan adına gelen istekler kabul edilir. Diğer alan adları `404` alır. |
| `allowedRoutes.namespaces.from: Same` | Sadece Gateway ile aynı namespace'teki (`week6`) HTTPRoute'lar bağlanabilir. Başka bir ekibin namespace'i bu Gateway'e route ekleyemez. |

```bash
kubectl apply -f 2-gateway.yaml
```

---

## 3. HTTPRoute

[`3-httproute.yaml`](3-httproute.yaml)

```yaml
apiVersion: gateway.networking.k8s.io/v1
kind: HTTPRoute
metadata:
  name: week6-api
  namespace: week6
spec:
  parentRefs:
    - name: week6-gateway
  hostnames:
    - order.kiteloomstudio.com
  rules:
    - matches:
        - path:
            type: PathPrefix
            value: /
      backendRefs:
        - name: week6-api
          port: 80
```

| Alan | Ne yapar |
|---|---|
| `parentRefs` | Kuralın bağlandığı Gateway |
| `hostnames` | Kuralın geçerli olduğu alan adı. Gateway listener'ının `hostname`'iyle uyuşmalıdır. |
| `matches.path` | `PathPrefix: /` tüm path'leri eşler: `/`, `/health` ve diğerleri |
| `backendRefs` | İsteğin gönderileceği Service ve portu. `week6-api` Service'inin `port: 80` değeri. |

```bash
kubectl apply -f 3-httproute.yaml
```

---

## 4. Durumu doğrulayın

```bash
kubectl -n week6 get gateway,httproute
```

```text
NAME                                              CLASS     ADDRESS        PROGRAMMED
gateway.gateway.networking.k8s.io/week6-gateway   traefik   2.31.140.127   True

NAME                                            HOSTNAMES
httproute.gateway.networking.k8s.io/week6-api   ["order.kiteloomstudio.com"]
```

- `ADDRESS`: Gateway'e ulaşılacak IP. Traefik bunu kendi Service'inin LB IP'sinden alır.
- `PROGRAMMED: True`: Traefik Gateway'i yapılandırdı ve trafik almaya hazır.

Ayrıntılı durum:

```bash
kubectl -n week6 get gateway week6-gateway -o jsonpath='{range .status.conditions[*]}{.type}={.status}{"\n"}{end}{range .status.listeners[*]}listener {.name}: attachedRoutes={.attachedRoutes}{"\n"}{end}'
```

```text
Accepted=True
Programmed=True
listener http: attachedRoutes=1
```

```bash
kubectl -n week6 get httproute week6-api -o jsonpath='{range .status.parents[*].conditions[*]}{.type}={.status}{"\n"}{end}'
```

```text
Accepted=True
ResolvedRefs=True
```

| Durum | Anlamı | `False` ise |
|---|---|---|
| Gateway `Accepted` | Controller Gateway'i kabul etti | `gatewayClassName` yanlış veya GatewayClass yok |
| Gateway `Programmed` | Trafik almaya hazır | Listener portu Traefik entrypoint'iyle uyuşmuyor |
| `attachedRoutes=1` | Bir HTTPRoute bu listener'a bağlandı | `0` ise HTTPRoute'un `parentRefs` veya `hostnames` değeri uyuşmuyor |
| HTTPRoute `Accepted` | Route Gateway'e bağlandı | `allowedRoutes` route'un namespace'ine izin vermiyor |
| HTTPRoute `ResolvedRefs` | `backendRefs`'teki Service bulundu | Service adı veya portu yanlış |

---

## 5. DNS kaydı olmadan test

DNS kaydı eklenmeden önce de yönlendirme test edilebilir. Traefik isteği `Host` header'ına göre
yönlendirir. `curl` ile header'ı elle verin:

```bash
curl -H "Host: order.kiteloomstudio.com" http://2.31.140.127/
```

```json
{"message":"Week6.API çalışıyor","version":"sha-15b32c4","pod":"week6-api-85c99847f8-7vshm"}
```

Başka bir alan adı veya sadece IP ile gelen istekler hiçbir route'a uymaz:

```bash
curl -s -o /dev/null -w "%{http_code}\n" -H "Host: other.kiteloomstudio.com" http://2.31.140.127/
curl -s -o /dev/null -w "%{http_code}\n" http://2.31.140.127/
```

```text
404
404
```

Bu, Gateway listener'ındaki `hostname` filtresinin çalıştığını gösterir.

---

## 6. DNS kaydı (Cloudflare)

`kiteloomstudio.com` alan adının DNS'i Cloudflare'dedir:

```bash
dig +short NS kiteloomstudio.com
```

```text
rustam.ns.cloudflare.com.
lia.ns.cloudflare.com.
```

Cloudflare Dashboard'da kayıt ekleyin: kiteloomstudio.com > DNS > Records > **Add record**

| Alan | Değer |
|---|---|
| Type | `A` |
| Name | `order` |
| IPv4 address | `2.31.140.127` (Traefik LB IP'si) |
| Proxy status | **DNS only** (gri bulut) |
| TTL | Auto |

**Neden DNS only?** Proxy (turuncu bulut) açıkken istekler Cloudflare üzerinden gelir. Cloudflare'in
SSL ayarı `Full` veya `Full (strict)` ise Cloudflare sunucuya HTTPS ile bağlanmaya çalışır. Gateway'de
henüz HTTPS listener'ı olmadığı için bağlantı başarısız olur. DNS only ile tarayıcı doğrudan Hetzner
LB'sine bağlanır.

İsterseniz IPv6 için aynı adla bir `AAAA` kaydı da ekleyin: `2a01:4f8:c016:6cd3::1`.

Kaydın yayıldığını kontrol edin:

```bash
dig +short order.kiteloomstudio.com
```

```text
2.31.140.127
```

---

## 7. Alan adıyla erişin

```bash
curl http://order.kiteloomstudio.com/
```

Tarayıcıdan: http://order.kiteloomstudio.com/

---

## HTTPS

Bu rehberdeki Gateway sadece HTTP dinler. HTTPS için Gateway'e bir `HTTPS` listener'ı ve bir TLS
sertifikası (`certificateRefs`) eklemek gerekir. Sertifika genellikle cert-manager ve Let's Encrypt ile
otomatik alınır. Bu ayrı bir konudur.

## Sorun giderme

| Belirti | Olası neden | Kontrol |
|---|---|---|
| `kubectl get gatewayclass` boş | Traefik kapalı veya Gateway provider açık değil | `kubectl -n kube-system get helmchartconfig traefik -o yaml`, `kubectl -n kube-system get deploy traefik` |
| Traefik Service `EXTERNAL-IP` `<pending>` | LB annotation'ları eksik | `kubectl -n kube-system describe svc traefik` olayları |
| Gateway `PROGRAMMED: False` | Listener portu `8000` değil | `kubectl -n week6 describe gateway week6-gateway` |
| `attachedRoutes=0` | HTTPRoute `parentRefs` veya `hostnames` uyuşmuyor | `kubectl -n week6 describe httproute week6-api` |
| `curl` `404` dönüyor | `Host` header'ı yok veya farklı alan adı | `curl -H "Host: order.kiteloomstudio.com" http://2.31.140.127/` |
| Alan adı çözülmüyor | DNS kaydı yok veya yayılmadı | `dig +short order.kiteloomstudio.com` |
| Cloudflare `521` / `525` hatası | Proxy açık ve Cloudflare HTTPS ile bağlanmaya çalışıyor | Kaydı **DNS only** yapın |

## Temizlik

Gateway ve HTTPRoute'u silin:

```bash
kubectl delete -f 3-httproute.yaml -f 2-gateway.yaml
```

Traefik'i kapatmak için `hetzner-k3s/cluster.yaml` içinde `addons.traefik.enabled: false` yapıp
`create` komutunu çalıştırın. Traefik Service'i silinince CCM `traefik` LB'sini de siler. Kontrol edin:

```bash
hcloud load-balancer list
```

Cloudflare'deki `order` DNS kaydını da silin.
