# Hospedagem da Jaq para celular

Preparação para um único salão em servidor Linux com Docker Compose. Ainda não publicada: falta escolher o servidor, conectar a conta e definir um domínio. O sistema local continua funcionando.

## O que o salão precisa

Um servidor com armazenamento persistente, um domínio ou subdomínio, e uma cópia externa das cópias de segurança. Depois da publicação, abrir o endereço HTTPS no navegador do celular e adicionar à tela inicial. Cada pessoa entra com seu usuário; o computador do salão não precisa ficar ligado.

## Instalação pelo responsável técnico

1. Instalar Docker Engine e o plugin Compose no servidor. Apontar o DNS do domínio para o servidor. Liberar portas TCP 80 e 443; restringir SSH à administração. Não abrir 8080 ou 5187 para a internet.
2. Transferir o código para uma pasta privada no servidor. Entrar em `hosting`, copiar `.env.example` para `.env` e preencher o domínio real, sem `https://` ou caminho, e um e-mail válido para o certificado.
3. Em uma janela combinada com o salão, encerrar a versão local usando `Parar.cmd` e copiar toda a pasta `JaqAplicativo/App_Data` para `hosting/data` por canal privado, como SFTP. Manter o original guardado. Não publicar o banco nem as chaves em repositórios ou arquivos de distribuição. Retirar apenas `.instance.lock` da cópia, se presente.
4. Ajustar a permissão de `hosting/data` para UID/GID 1654, o usuário `app` da imagem .NET, e limitar acesso. Executar como administrador no servidor:

```sh
sudo chown -R 1654:1654 data
sudo find data -type d -exec chmod 700 {} +
sudo find data -type f -exec chmod 600 {} +
chmod 600 .env
docker compose config --quiet
docker compose up -d --build
docker compose logs --tail 100 app https
```

O aplicativo recusa iniciar remotamente com banco ausente ou sem dona ativa já configurada. Isso evita expor a criação da primeira conta na internet. A configuração admite um único endereço. A rede privada Docker usa `172.30.84.0/24`; se conflitar com outra rede do servidor, alterar a rede, o IP do serviço HTTPS e `JAQ_TRUSTED_PROXY` em conjunto. Apenas esse proxy pode encaminhar a identificação HTTPS e o IP da cliente. Não habilitar confiança em todos os proxies.

5. Conferir no domínio o login já existente, os cadastros transferidos e os valores. Testar no celular pelo 4G/5G, agenda, vários serviços e permissões das atendentes. Enquanto a cópia é conferida, manter a operação local pausada. Depois da migração, usar somente a versão online: dois bancos independentes não sincronizam.
6. Guardar o banco local como cópia histórica. Nunca apagar antes de conferir a migração.

## Cópias de segurança e atualização

Executar `sh backup.sh` antes de atualizações e diariamente. O script interrompe brevemente a aplicação, copia banco e chaves juntos e reinicia o serviço mesmo em caso de falha. Os arquivos ficam em `hosting/backups` com permissão privada. A rotina diária precisa ser agendada no servidor escolhido; não está ativa nesta entrega. Transferir uma cópia para armazenamento privado fora do servidor e conferir periodicamente a restauração em ambiente separado. Backup no mesmo disco não cobre perda do servidor.

Para atualizar, fazer backup, atualizar o código e executar `docker compose up -d --build app`. Não remover `data` nem volumes de certificados. A aplicação deve rodar em uma única instância, pois usa SQLite e bloqueia dois processos sobre o mesmo banco.

As chaves de sessão persistem em `data/keys`; ficam protegidas pelas permissões do servidor, sem criptografia adicional da aplicação. Escolher armazenamento do servidor e backups com criptografia e acesso restrito. Não habilitar registro dos corpos de requisições ou das senhas.

## Validação desta preparação

Código compilado e testes de modo remoto executáveis em `tests/remote-hosting.py`. Os testes simulam o proxy HTTPS em banco isolado: domínio, bloqueio de HTTP, cookies seguros, login existente, limite por IP e rejeição de proxy desconhecido. Docker não está instalado no computador de desenvolvimento: montagem dos contêineres, emissão do certificado, backup Linux e publicação precisam ser verificados no servidor antes de liberar o uso real. Nenhum dado do salão está dentro dos pacotes.

Configuração baseada nas documentações oficiais de [proxy no ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0), [imagens .NET](https://github.com/dotnet/dotnet-docker/blob/main/README.aspnet.md), [HTTPS automático](https://caddyserver.com/docs/automatic-https) e [proxy Caddy](https://caddyserver.com/docs/caddyfile/directives/reverse_proxy).
