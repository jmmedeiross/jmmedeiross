# Jaq Alongamentos - gestão de salão em C#/.NET

Aplicação Full Stack para um salão especializado em loiros e mega hair. Uma cliente pode receber vários serviços de profissionais diferentes na mesma comanda, com preço e comissão registrados por item.

**Stack:** C#, .NET 10, ASP.NET Core Minimal APIs, Entity Framework Core, SQLite, JavaScript, HTML e CSS.

## O que o projeto resolve

- Agenda por profissional com bloqueio de sobreposição de horários.
- Ficha técnica da cliente para procedimentos químicos e mega hair, com acompanhamento de retornos.
- Comandas com várias profissionais, vários serviços por profissional e histórico dos valores praticados.
- Sinais e recebimentos manuais, saldo da comanda e apuração de comissões.
- Perfis de dona, gerente, recepção e atendente. A gerente pode cadastrar e desativar atendentes sem apagar o histórico.
- Histórico de alterações apresentado em português e interface responsiva para celular.

## Decisões técnicas

Dinheiro é armazenado em centavos inteiros. Alterar o catálogo não muda os valores de serviços já registrados. Revisões de registros evitam sobrescrever alterações concorrentes; chaves de operação evitam cadastros duplicados. Uma seção serializada protege verificações de agenda e saldo, e um bloqueio de arquivo impede duas instâncias sobre o mesmo banco SQLite.

O acesso usa senha com hash, sessão HttpOnly/SameSite, proteção CSRF e limitação de tentativas. Atendentes consultam apenas os atendimentos vinculados a elas. No modo remoto, o sistema exige domínio específico, proxy confiável, HTTPS e uma conta da dona previamente configurada.

## Executar a demonstração

Com o SDK .NET 10 instalado, entre nesta pasta:

```sh
dotnet restore
dotnet run --urls http://127.0.0.1:5187
```

Abra o endereço local e configure a conta da dona. Não há credenciais padrão. O banco novo fica em `App_Data`, ignorado pelo Git. `JAQ_DATA_DIR` permite usar outra pasta. Os 14 registros iniciais de equipe são provisórios e não representam pessoas reais.

**O catálogo deste repositório contém valores fictícios de demonstração.** Não inclui banco, clientes, usuários reais, senhas ou chaves da instalação do salão. Antes do uso operacional, cadastrar e confirmar a tabela própria.

## Verificação

```sh
python tests/run.py
python tests/remote-hosting.py
```

Os testes usam banco separado: múltiplas profissionais, valores e comissões, pagamentos, agenda, permissões, concorrência, repetição de cadastros, revogação de sessões e preservação dos dados após reiniciar. O teste remoto simula um proxy HTTPS e verifica cookies, domínio e limite de tentativas por IP.

Os testes opcionais de interface precisam de Node.js e Playwright: `python tests/run.py --browser`. Eles verificam as oito telas em larguras de 320, 390, 768 e 1440 pixels, perda de conexão e sessões expiradas. Não confundir grupos de cenários com quantidade de testes unitários.

## Estado e limites

A instalação do salão funciona localmente. A configuração para internet está preparada em [hosting](hosting/README.md), com Docker, Caddy, dados persistentes e roteiro de migração. Publicação e certificado público aguardam validação no servidor; os contêineres não foram executados no ambiente de desenvolvimento.

Este projeto atende um único salão. Assinatura SaaS e isolamento entre salões ainda não foram implementados. Recebimentos são registros manuais, sem movimentação bancária. Integração WhatsApp, notas fiscais, estoque, fotos de clientes, sessões de pacotes, descontos e estornos são evoluções futuras. Comissões são apuradas após concluir o serviço, independentemente da quitação da cliente.
