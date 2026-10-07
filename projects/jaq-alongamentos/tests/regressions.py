import concurrent.futures, json, os, re, sqlite3, subprocess, uuid
from pathlib import Path
exec(Path(__file__).with_name('integration.py').read_text(encoding='utf-8').split('owner=Session()')[0])
owner=Session();owner.login('dona.teste');checks=[]
data=owner.data();own=next(s for s in data['staff'] if s['role']=='owner')
def staff_body(s,**changes):
    body={k:s[k] for k in ('name','role','active','canProvide','username')};body['password']='';body.update(changes);return body

owner.call('/staff/'+str(own['id']),'PUT',staff_body(own,username=None),expected=400)
owner.call('/staff/'+str(own['id']),'PUT',staff_body(own,active=False),expected=400)
owner.call('/staff/'+str(own['id']),'PUT',staff_body(own,role='manager'),expected=400)
assert owner.call('/session')[1]['user']['role']=='owner'
owner.call('/staff','POST',{'name':'Sem senha','role':'attendant','active':True,'canProvide':True,'username':'sem.senha'},expected=400)
owner.call('/staff','POST',{'name':'Duplicado','role':'attendant','active':True,'canProvide':True,'username':'dona.teste','password':'Outra.Teste.2026!'},expected=409)
owner.call('/staff','POST',{'name':'Campos incompletos'},expected=400)
checks.append('Acesso da dona preservado; usuário sem senha, duplicado ou incompleto rejeitado')
manager_row=next(s for s in owner.data()['staff'] if s['role']=='manager')
previous_audits=len(owner.data()['audits']);previous_revision=manager_row['revision']
owner.call('/staff/'+str(manager_row['id']),'PUT',staff_body(manager_row))
current=owner.data()
assert len(current['audits'])==previous_audits
assert next(s for s in current['staff'] if s['id']==manager_row['id'])['revision']==previous_revision
owner.call('/staff/'+str(manager_row['id']),'PUT',staff_body(manager_row,canProvide=not manager_row['canProvide']))
entry=owner.data()['audits'][0]
assert entry['action']=='staff.update' and 'Cadastro de Gerente atualizado:' in entry['detail']
assert 'True' not in entry['detail'] and 'perfil=manager' not in entry['detail']
assert ('habilitada para realizar serviços' if not manager_row['canProvide'] else 'atendimento de serviços desabilitado') in entry['detail']
checks.append('Histórico da equipe em português; salvar sem mudança não cria evento nem revoga sessão')


client=owner.data()['clients'][0];body={k:v for k,v in client.items() if k!='revision'}
owner.call('/clients/'+str(client['id']),'PUT',body,expected=428,revision=None)
owner.call('/clients/'+str(client['id']),'PUT',body,expected=409,revision='desatualizado')
old=client['revision'];body['preferences']='Alteração mais recente'
owner.call('/clients/'+str(client['id']),'PUT',body,revision=old)
body['preferences']='Sobrescrita antiga'
owner.call('/clients/'+str(client['id']),'PUT',body,expected=409,revision=old)
assert next(c for c in owner.data()['clients'] if c['id']==client['id'])['preferences']=='Alteração mais recente'
service=owner.data()['services'][0];sb={k:service[k] for k in ('name','price','payout','reviewed','notes')}
owner.call('/services/'+str(service['id']),'PUT',sb,expected=409,revision='antigo')
owner.call('/services/'+str(service['id']),'PUT',sb,expected=428,revision=None)
staff=owner.data()['staff'][0]
owner.call('/staff/'+str(staff['id']),'PUT',staff_body(staff),expected=409,revision='antigo')
checks.append('Revisões obrigatórias impedem sobrescrita de clientes, serviços e equipe')

key=str(uuid.uuid4());body={'name':'Cadastro idempotente','phone':'000000000'}
before=len(owner.data()['clients']);_,one=owner.call('/clients','POST',body,key=key);_,two=owner.call('/clients','POST',body,key=key)
assert one==two and len(owner.data()['clients'])==before+1
owner.call('/clients','POST',{'name':'Outro conteúdo'},key=key,expected=409)
vk=str(uuid.uuid4());vb={'clientId':one['id'],'notes':'Comanda única'}
_,v1=owner.call('/visits','POST',vb,key=vk);_,v2=owner.call('/visits','POST',vb,key=vk);assert v1==v2
sk=str(uuid.uuid4());staff_input={'name':'Acesso idempotente','role':'attendant','active':True,'canProvide':True,'username':'idempotente','password':'Jaq.Teste.2026!'}
_,s1=owner.call('/staff','POST',staff_input,key=sk);_,s2=owner.call('/staff','POST',staff_input,key=sk);assert s1==s2
staff_input['password']='Diferente.Teste.2026!';owner.call('/staff','POST',staff_input,key=sk,expected=409)
owner.call('/clients','POST',{'name':'Chave inválida'},key='invalid',expected=400)
checks.append('Repetir o mesmo cadastro não duplica cliente, comanda ou acesso; alterações de conteúdo/senha são detectadas')

sid=next(s['id'] for s in owner.data()['services'] if s['name']=='Hidratação')
vid=v1['id']
item={'serviceId':sid,'staffId':3,'startsAt':'2032-11-05T09:00:00','minutes':60,'payout':None}
for change in ({'minutes':4},{'minutes':721},{'minutes':12.5},{'startsAt':'2032-11-05T09:00:30'},{'startsAt':'2032-11-05T09:00:00Z'},{'startsAt':'2032-11-05T09:00:00-03:00'},{'startsAt':'2101-11-05T09:00:00'}):
    owner.call(f'/visits/{vid}/items','POST',{**item,**change},expected=400)
owner.call(f'/visits/{vid}/items','POST',item)
owner.call(f'/visits/{vid}/items','POST',{**item,'startsAt':'2032-11-05T10:00:00'})
owner.call(f'/visits/{vid}/items','POST',{**item,'startsAt':'2032-11-05T09:59:00'},expected=409)
owner.call(f'/visits/{vid}/items','POST',{**item,'staffId':4})
owner.call(f'/visits/{vid}/items','POST',{**item,'staffId':own['id'],'startsAt':'2032-11-05T12:00:00'},expected=400)
owner.call(f'/visits/{vid}/items','POST',{**item,'staffId':own['id'],'startsAt':'2032-11-05T12:00:00','payout':0})
checks.append('Horários e durações inválidos rejeitados; serviços adjacentes e profissionais simultâneas permitidos; dona exige valor explícito')
owner_item=next(i for v in owner.data()['visits'] if v['id']==vid for i in v['items'] if i['staffId']==own['id'])
assignment={'staffId':5,'startsAt':owner_item['startsAt'],'minutes':owner_item['minutes'],'payout':None}
owner.call(f"/visits/{vid}/items/{owner_item['id']}",'PUT',assignment,expected=400)
owner.call(f"/visits/{vid}/items/{owner_item['id']}",'PUT',{**assignment,'payout':7500})
updated=next(i for v in owner.data()['visits'] if v['id']==vid for i in v['items'] if i['id']==owner_item['id'])
assert updated['staffId']==5 and updated['payout']==7500
checks.append('Transferência de serviço da dona exige nova comissão explícita, sem reaproveitar zero automaticamente')


for changed in ({'price':-1},{'price':100000001},{'price':1.5},{'payout':sb['price']+1}):
    owner.call('/services/'+str(service['id']),'PUT',{**sb,**changed},expected=400)
owner.call('/clients/'+str(client['id']),'PUT',{**body,'name':''},expected=400)
owner.call('/clients/'+str(client['id']),'PUT',{**body,'preferences':'x'*2001},expected=400)
owner.call('/clients/'+str(client['id']),'PUT',{**body,'nextReturn':'2101-01-01'},expected=400)
owner.call('/clients/'+str(client['id']),'PUT',{**body,'installationDate':'0001-01-01'},expected=400)
owner.call(f'/visits/{vid}/payments','POST',{'amount':-1,'method':'Pix','kind':'payment'},expected=400)
owner.call(f'/visits/{vid}/payments','POST',{'amount':1,'method':'Outro','kind':'payment'},expected=400)
owner.call(f'/visits/{vid}/payments','POST',{'amount':1,'method':'Pix','kind':'Outro'},expected=400)
owner.call('/clients','POST',None,expected=400)
owner.call('/rota-inexistente',expected=404)
owner.call('/rota-inexistente','POST',{},expected=404)
checks.append('Valores, datas, textos, JSON nulo e rotas desconhecidas recebem erros controlados')

current=next(v for v in owner.data()['visits'] if v['id']==vid)
revision=current['version']
def payment():
    return owner.call(f'/visits/{vid}/payments','POST',{'amount':1000,'method':'Pix','kind':'deposit'},expected=200,revision=revision)
# Use independent cookie sessions to exercise concurrent server requests.
other_session=Session();other_session.login('dona.teste')
def raw_payment(session):
    try:session.call(f'/visits/{vid}/payments','POST',{'amount':1000,'method':'Pix','kind':'deposit'},revision=revision);return 200
    except AssertionError as ex:
        assert ex.args[0][1]==409,ex;return 409
with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:codes=list(pool.map(raw_payment,[owner,other_session]))
assert sorted(codes)==[200,409]
now=next(v for v in owner.data()['visits'] if v['id']==vid);assert sum(p['amount'] for p in now['payments'])==1000
checks.append('Duas gravações simultâneas com a mesma revisão produzem um único recebimento')

db_path=Path(os.environ['JAQ_TEST_DB']);db=sqlite3.connect(db_path)
assert db.execute('pragma integrity_check').fetchone()[0]=='ok'
assert db.execute('pragma foreign_key_check').fetchall()==[]
db.execute('update Staff set Role=? where Id=?',('attendant',own['id']));db.commit()
try:other_session.call('/data',expected=401)
finally:db.execute('update Staff set Role=? where Id=?',('owner',own['id']));db.commit()
assert owner.call('/session')[1]['user']['role']=='owner'
checks.append('Perfil da sessão é conferido com o banco, mesmo quando o contador de segurança não muda')

assert not any('Password' in audit['detail'] or 'password' in audit['detail'] for audit in owner.data()['audits'])
assert not any('passwordHash' in s or 'securityVersion' in s for s in owner.data()['staff'])
source=Path(__file__).resolve().parents[1];env=os.environ.copy();env['JAQ_DATA_DIR']=str(db_path.parent.resolve())
second=subprocess.run(['dotnet',str(source/'bin/Debug/net10.0/JaqAlongamentos.dll'),'--urls','http://127.0.0.1:5199'],cwd=source,env=env,capture_output=True,timeout=10)
assert second.returncode!=0 and 'IOException' in second.stderr.decode(errors='replace')
assert owner.call('/session')[1]['user']['role']=='owner'
checks.append('Banco íntegro, referências válidas, senhas não expostas e segunda instância no mesmo banco bloqueada')

seed=json.loads((Path(__file__).resolve().parents[1]/'Seed/services.json').read_text(encoding='utf-8'))
from decimal import Decimal
assert len(seed)==54 and len({s['id'] for s in seed})==54
assert all(Decimal(s['valor_cobrado_cliente_brl'])>=Decimal(s['valor_recebido_atendente_brl'])>=0 for s in seed)
assert [s['id'] for s in seed if Decimal(s['valor_recebido_atendente_brl'])!=Decimal(s['valor_cobrado_cliente_brl'])*Decimal('.3')]==['SRV-014']
checks.append('54 serviços e comissões conferidos; exceção de R$40 preservada para revisão humana')
Path(os.environ.get('JAQ_REGRESSION_RESULTS','work/audit-regression-results.json')).write_text(json.dumps(checks,ensure_ascii=False,indent=2),encoding='utf-8')
print('PASS: '+str(len(checks))+' grupos adicionais de regressão.')
