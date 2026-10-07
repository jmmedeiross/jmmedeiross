import json, urllib.request, urllib.error, http.cookiejar, sqlite3, os, uuid, re
from pathlib import Path

BASE=os.environ.get('JAQ_TEST_URL','http://127.0.0.1:5188')+'/api'
checks=[]
class Session:
    def __init__(self):
        self.jar=http.cookiejar.CookieJar()
        self.opener=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.jar))
        self.token=self.call('/csrf')[1]['token']
    def call(self,path,method='GET',body=None,expected=200,csrf=True,revision='auto',key=None):
        headers={'Content-Type':'application/json'}
        if method!='GET' and csrf:headers['X-CSRF-TOKEN']=self.token
        if method!='GET' and revision=='auto' and (re.match(r'^/(staff|services|clients)/\d+$',path) or re.match(r'^/visits/\d+/',path) or path.startswith('/commissions/')):
            snapshot=self.call('/data')[1]
            match=re.match(r'^/(staff|services|clients)/(\d+)$',path)
            if match:
                row=next((row for row in snapshot[match[1]] if row['id']==int(match[2])),None)
                value=row['revision'] if row else None
            else:
                match=re.match(r'^/visits/(\d+)/',path)
                if match:row=next((row for row in snapshot['visits'] if row['id']==int(match[1])),None)
                else:
                    item_id=int(path.split('/')[2]);row=next((row for row in snapshot['visits'] if any(i['id']==item_id for i in row['items'])),None)
                value=row['version'] if row else None
            if value is not None:headers['If-Match']='"'+str(value)+'"'
        elif revision is not None and revision!='auto':headers['If-Match']='"'+str(revision)+'"'
        if method=='POST' and path in ('/clients','/staff','/visits'):headers['Idempotency-Key']=key or str(uuid.uuid4())
        req=urllib.request.Request(BASE+path,method=method,headers=headers,data=json.dumps(body).encode() if body is not None else None)
        try:
            with self.opener.open(req) as response:
                code=response.status;raw=response.read()
        except urllib.error.HTTPError as error:
            code=error.code;raw=error.read()
        assert code==expected,(path,code,expected,raw.decode()[:500])
        return code,json.loads(raw) if raw else None
    def login(self,name):
        self.call('/login','POST',{'username':name,'password':'Jaq.Teste.2026!'})
        self.token=self.call('/csrf')[1]['token']
    def data(self):return self.call('/data')[1]

owner=Session()
assert owner.call('/session')[1]['setupNeeded'] is True,'Execute em um banco novo de testes, nunca no banco do salão.'
owner.call('/data',expected=401)
owner.call('/setup','POST',{'name':'Dona Teste','username':'dona.teste','password':'Jaq.Teste.2026!'},expected=400,csrf=False)
owner.call('/setup','POST',{'name':'Dona Teste','username':'dona.teste','password':'Jaq.Teste.2026!'})
owner.token=owner.call('/csrf')[1]['token']
owner.call('/setup','POST',{'name':'Outra','username':'outra','password':'Jaq.Teste.2026!'},expected=409)
data=owner.data()
assert len(data['staff'])==14 and len(data['services'])==54 and len(data['clients'])==0
checks.append('Configuração única, autenticação, CSRF e catálogo com 54 serviços / equipe de 14')

manager_id=next(s['id'] for s in data['staff'] if s['role']=='manager')
def staff_body(s,**changes):
    body={k:s[k] for k in ('name','role','active','canProvide','username')};body['password']='';body.update(changes);return body
person=next(s for s in data['staff'] if s['id']==manager_id)
owner.call('/staff/'+str(manager_id),'PUT',staff_body(person,username='gerente.teste',password='Jaq.Teste.2026!'))
attendant=next(s for s in data['staff'] if s['id']==1)
owner.call('/staff/1','PUT',staff_body(attendant,username='atendente.teste',password='Jaq.Teste.2026!'))
manager=Session();manager.login('gerente.teste')
manager.call('/staff','POST',{'name':'Atendente extra','role':'attendant','active':True,'canProvide':True,'username':None,'password':None})
manager.call('/staff/'+str(manager_id),'PUT',staff_body(person),expected=403)
manager.call('/staff','POST',{'name':'Escalada','role':'owner','active':True,'canProvide':True,'username':None,'password':None},expected=403)
checks.append('Gerente adiciona atendentes sem elevar perfis ou alterar conta de gerente')

_,client=manager.call('/clients','POST',{'name':'Cliente de teste <script>','phone':'11999999999','formula':'Registro técnico de exemplo','nextReturn':'2030-10-10'})
_,other=manager.call('/clients','POST',{'name':'Cliente sem vínculo'})
_,visit=manager.call('/visits','POST',{'clientId':client['id'],'notes':'Visita de teste'})
vid=visit['id']
services={s['name']:s for s in data['services']}
selected=[('Mechas cabelo todo — longo',1,'2030-10-06T09:00:00',180),('Hidratação',2,'2030-10-06T09:00:00',30),('Escova Joico — lisa ou modelada',2,'2030-10-06T09:30:00',30)]
for name,staff,start,duration in selected:
    manager.call(f'/visits/{vid}/items','POST',{'serviceId':services[name]['id'],'staffId':staff,'startsAt':start,'minutes':duration,'payout':None})
_,second=manager.call('/visits','POST',{'clientId':other['id'],'notes':''})
manager.call(f"/visits/{second['id']}/items",'POST',{'serviceId':services['Hidratação']['id'],'staffId':1,'startsAt':'2030-10-06T10:00:00','minutes':60,'payout':None},expected=409)
v=next(v for v in manager.data()['visits'] if v['id']==vid)
assert sum(i['price'] for i in v['items'])==163000
assert sum(i['payout'] for i in v['items'] if i['staffId']==1)==36000
assert sum(i['payout'] for i in v['items'] if i['staffId']==2)==12900
checks.append('Comanda com várias atendentes e vários serviços por atendente; totais 1630/360/129; conflitos bloqueados')

manager.call(f'/visits/{vid}/payments','POST',{'amount':30000,'method':'Pix','kind':'deposit'})
manager.call(f'/visits/{vid}/payments','POST',{'amount':133001,'method':'Pix','kind':'payment'},expected=400)
manager.call(f'/visits/{vid}/payments','POST',{'amount':133000,'method':'Cartão de crédito','kind':'payment'})
v=next(v for v in manager.data()['visits'] if v['id']==vid)
assert sum(p['amount'] for p in v['payments'])==163000
manager.call(f"/visits/{vid}/items/{v['items'][0]['id']}",'DELETE',expected=400)
manager.call(f'/visits/{vid}/status','POST',{'status':'cancelled'},expected=400)
svc=services['Mechas cabelo todo — longo']
manager.call('/services/'+str(svc['id']),'PUT',{'name':svc['name'],'price':130000,'payout':39000,'reviewed':True,'notes':'Teste de preservação'})
v=next(v for v in manager.data()['visits'] if v['id']==vid)
assert v['items'][0]['price']==120000 and v['items'][0]['payout']==36000
checks.append('Sinal abatido uma vez, excesso e remoção com saldo negativo bloqueados; catálogo preserva valores da comanda')

att=Session();att.login('atendente.teste')
ad=att.data();assert len(ad['clients'])==1 and len(ad['visits'])==1
assert all(i['staffId']==1 for v in ad['visits'] for i in v['items'])
assert all(v['payments']==[] for v in ad['visits'])
att.call('/clients/'+str(other['id']),'PUT',{'name':'Tentativa'},expected=403)
att.call('/staff','POST',{'name':'Tentativa','role':'attendant','active':True,'canProvide':True},expected=403)
att.call('/clients/'+str(client['id']),'PUT',{'name':'Cliente de teste <script>','formula':'Atualizada pela atendente','nextReturn':'2030-10-10'})
checks.append('Atendente vê apenas suas clientes, serviços e comissões; edição de ficha com vínculo; gestão bloqueada')

active=next(s for s in manager.data()['staff'] if s['id']==1)
manager.call('/staff/1','PUT',staff_body(active,active=False))
att.call('/data',expected=401)
manager.call(f'/visits/{vid}/status','POST',{'status':'completed'},expected=400)
manager.call('/staff/1','PUT',staff_body(active,active=True))
manager.call(f'/visits/{vid}/status','POST',{'status':'completed'})
manager.call(f'/visits/{vid}/items','POST',{'serviceId':services['Hidratação']['id'],'staffId':2,'startsAt':'2030-10-06T12:00:00','minutes':30},expected=400)
for item in v['items']:manager.call('/commissions/'+str(item['id'])+'/pay','POST')
manager.call('/commissions/'+str(v['items'][0]['id'])+'/pay','POST',expected=400)
checks.append('Desativação revoga sessão sem apagar histórico; fechamento imutável e pagamento de comissão sem duplicação')

db=sqlite3.connect(Path(os.environ.get('JAQ_TEST_DB','work/test-data/jaq.db')))
assert db.execute('select count(*) from Visits').fetchone()[0]==2
assert db.execute('select sum(Amount) from Payments').fetchone()[0]==163000
assert db.execute('select count(*) from Audits').fetchone()[0]>=20
checks.append('Dados persistidos em SQLite e alterações auditadas')
Path(os.environ.get('JAQ_TEST_RESULTS','work/integration-results.json')).write_text(json.dumps(checks,ensure_ascii=False,indent=2),encoding='utf-8')
print('PASS: '+str(len(checks))+' grupos de verificações de negócio e acesso.')
