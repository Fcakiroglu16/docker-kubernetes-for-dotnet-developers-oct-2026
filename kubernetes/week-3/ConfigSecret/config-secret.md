# ConfigMap ve Secret'ı birlikte kullanmak

Gerçek bir uygulamada ayarların bir kısmı gizli değildir (mod, limitler), bir kısmı gizlidir (şifre, token). Gizli olmayanlar **ConfigMap**'te, gizli olanlar **Secret**'ta tutulur. Pod ikisini de ortam değişkeni olarak okur. Bu örnekte `Microservice1.API` iki kaynaktan gelen değerleri okur ve `/config` endpoint'i ile gösterir.

ConfigMap ve Secret'ın ayrıntıları için bkz. [configmap.md](../ConfigMap/configmap.md) ve [secret.md](../Secret/secret.md).

| Dosya | İçeriği |
|---|---|
| [configmap.yaml](configmap.yaml) | `App__Mode=demo`, `App__MaxItems=10` (gizli olmayan ayarlar) |
| [secret.yaml](secret.yaml) | `Database__User`, `Database__Password` (gizli bilgiler, demo değerleri) |
| [pod.yaml](pod.yaml) | `Microservice1.API` Pod'u. `envFrom` ile ikisini de ortam değişkeni olarak alır |

> .NET'te `App:Mode` gibi hiyerarşik bir ayar, ortam değişkeninde `:` yerine `__` ile yazılır (`App__Mode`). ASP.NET Core bu ortam değişkenlerini `IConfiguration`'a otomatik ekler, API kodunda `config["App:Mode"]` ile okunur. Yani uygulamanın ortam değişkenini ayrıca okuması gerekmez.

## Adım 1: Image'ı build et

`/config` endpoint'i içeren yeni sürüm `1.7` olarak build edilir. Tüm komutlar repo kök klasöründen çalıştırılır. Image'ın kind cluster'ına nasıl ulaştığı için bkz. [pod.md](../pod/pod.md).

```bash
docker build -t microservice1-api:1.7 Microservice1.API
```

## Adım 2: ConfigMap'i oluştur

```bash
kubectl apply -f kubernetes/week-3/ConfigSecret/configmap.yaml
```

## Adım 3: Secret'ı oluştur

```bash
kubectl apply -f kubernetes/week-3/ConfigSecret/secret.yaml
```

İkisinin de oluştuğunu kontrol et:

```bash
kubectl get configmap microservice1-config -n ecommerce-dev
kubectl get secret microservice1-secret -n ecommerce-dev
```

## Adım 4: Pod'u oluştur

Pod, ConfigMap ve Secret'tan **sonra** oluşturulmalıdır. Yoksa Pod `CreateContainerConfigError` durumunda bekler.

```bash
kubectl apply -f kubernetes/week-3/ConfigSecret/pod.yaml
kubectl wait --for=condition=Ready pod/microservice1-config-pod -n ecommerce-dev --timeout=60s
```

## Adım 5: Ortam değişkenlerini kontrol et

Container içinde dört değişken de görünür. Secret'ın değeri container içinde düz metindir:

```bash
kubectl exec microservice1-config-pod -n ecommerce-dev -- printenv App__Mode App__MaxItems Database__User Database__Password
```

`describe` hangi kaynaklardan ortam değişkeni alındığını gösterir, değerleri göstermez:

```bash
kubectl describe pod microservice1-config-pod -n ecommerce-dev | grep -A3 'Environment Variables from'
```

## Adım 6: API'nin okuduğunu endpoint ile doğrula

Ayrı bir terminalde port-forward aç:

```bash
kubectl port-forward pod/microservice1-config-pod 8080:8080 -n ecommerce-dev
```

Başka bir terminalde:

```bash
curl http://localhost:8080/config
```

Beklenen çıktı:

```json
{"mode":"demo","maxItems":10,"dbUserSet":true,"dbPasswordSet":true,"dbPasswordLength":13}
```

- `mode` ve `maxItems` **ConfigMap**'ten gelir ve olduğu gibi gösterilir.
- `dbUserSet`, `dbPasswordSet` ve `dbPasswordLength` **Secret**'tan gelir. API Secret'ın değerini bilerek döndürmez, sadece tanımlı olup olmadığını ve uzunluğunu gösterir. Uygulamalar gizli değerleri asla yanıtta veya log'da yazmamalıdır.

## Adım 7: Değer değişince ne olur

ConfigMap'teki değeri değiştir:

```bash
kubectl patch configmap microservice1-config -n ecommerce-dev --type merge -p '{"data":{"App__MaxItems":"20"}}'
```

Ortam değişkenleri Pod başlarken bir kez okunur. Pod çalışırken değer **değişmez**, API hâlâ eski değeri (`10`) gösterir:

```bash
curl http://localhost:8080/config
```

Yeni değer için Pod'u silip yeniden oluştur. Port-forward Pod'a bağlı olduğu için önce `Ctrl+C` ile durdurulmalı, Pod yenilendikten sonra tekrar açılmalıdır:

```bash
kubectl delete pod microservice1-config-pod -n ecommerce-dev
kubectl apply -f kubernetes/week-3/ConfigSecret/pod.yaml
kubectl wait --for=condition=Ready pod/microservice1-config-pod -n ecommerce-dev --timeout=60s
```

```bash
kubectl port-forward pod/microservice1-config-pod 8080:8080 -n ecommerce-dev
```

Ayrı terminalde `maxItems` artık `20` olmalı:

```bash
curl http://localhost:8080/config
```

## Notlar

- İki kaynak da `envFrom` altında ayrı ayrı verilir. Aynı anahtar adı iki kaynakta da varsa hangisinin kazanacağına güvenme, anahtar adlarını çakıştırma.
- Daha az değişkeni almak için `envFrom` yerine tek tek `env` + `configMapKeyRef` / `secretKeyRef` kullanılabilir (bkz. [configmap.md](../ConfigMap/configmap.md) ve [secret.md](../Secret/secret.md)).
- Gizli değerleri ortam değişkeni yerine dosya olarak (volume) vermek daha güvenlidir, çünkü ortam değişkenleri çökme dökümlerine ve log'lara kolay sızar.
- Bu örnekteki Secret değerleri demo içindir. Gerçek bir projede Secret YAML'ını repoya koyma.

## Temizle

Port-forward'u `Ctrl+C` ile durdur, sonra:

```bash
kubectl delete pod microservice1-config-pod -n ecommerce-dev
kubectl delete configmap microservice1-config -n ecommerce-dev
kubectl delete secret microservice1-secret -n ecommerce-dev
```
