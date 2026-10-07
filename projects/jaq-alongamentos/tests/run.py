"""Runs the suite in a new isolated database; never uses the salon's database."""
import argparse, hashlib, http.cookiejar, json, os, socket, sqlite3, subprocess, sys, time, urllib.error, urllib.request, uuid
from pathlib import Path

root=Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser()
parser.add_argument('--no-build',action='store_true')
parser.add_argument('--browser',action='store_true',help='Also run Playwright browser tests')
parser.add_argument('--results',type=Path,default=root/'work'/'test-results')
args=parser.parse_args()
results=args.results.resolve();run=results/uuid.uuid4().hex[:12];run.mkdir(parents=True)
if not args.no_build:subprocess.run(['dotnet','build',str(root/'JaqAlongamentos.csproj')],check=True)
with socket.socket() as sock:sock.bind(('127.0.0.1',0));port=sock.getsockname()[1]
base=f'http://127.0.0.1:{port}'
env=os.environ.copy();env.update(JAQ_DATA_DIR=str(run/'data'),JAQ_TEST_URL=base,JAQ_TEST_DB=str(run/'data'/'jaq.db'),JAQ_TEST_RESULTS=str(run/'integration.json'),JAQ_REGRESSION_RESULTS=str(run/'regressions.json'),JAQ_BROWSER_RESULTS=str(run/'browser.json'),JAQ_RECOVERY_RESULTS=str(run/'recovery.json'),JAQ_TEST_SCREENSHOT_DIR=str(run),Logging__LogLevel__Default='Error')
log=(run/'server.log').open('w',encoding='utf-8');process=None
def start():
    child=subprocess.Popen(['dotnet',str(root/'bin/Debug/net10.0/JaqAlongamentos.dll'),'--urls',base],cwd=root,env=env,stdout=log,stderr=log)
    for attempt in range(100):
        if child.poll() is not None:raise RuntimeError('Test server exited. Read '+str(run/'server.log'))
        try:
            with urllib.request.urlopen(base+'/api/session',timeout=1) as response:
                assert json.load(response)['application']=='JaqAlongamentos';return child
        except (OSError,ValueError):time.sleep(.1)
    child.terminate();child.wait(timeout=5);raise RuntimeError('Test server did not start')
def stop(child):
    if child and child.poll() is None:child.terminate();child.wait(timeout=10)
def fingerprint():
    with sqlite3.connect('file:'+str(run/'data'/'jaq.db')+'?mode=ro',uri=True) as db:
        tables=['Staff','Services','Clients','Visits','VisitItem','Payments','Audits']
        names={r[0] for r in db.execute("select name from sqlite_master where type='table'")}
        tables=[name if name in names else 'Items' for name in tables]
        payload={name:db.execute('select * from "'+name+'" order by Id').fetchall() for name in tables}
        return hashlib.sha256(json.dumps(payload,ensure_ascii=False).encode()).hexdigest()
try:
    process=start()
    for filename in ('integration.py','regressions.py'):subprocess.run([sys.executable,str(root/'tests'/filename)],cwd=root,env=env,check=True)
    if args.browser:
        for filename in ('history-text.cjs','main-flow.cjs','recovery.cjs'):subprocess.run(['node',str(root/'tests'/filename)],cwd=root,env=env,check=True)
    jar=http.cookiejar.CookieJar();opener=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))
    token=json.load(opener.open(base+'/api/csrf'))['token'];codes=[]
    for attempt in range(12):
        request=urllib.request.Request(base+'/api/login',method='POST',headers={'Content-Type':'application/json','X-CSRF-TOKEN':token},data=json.dumps({'username':'não-existe','password':'incorreta'}).encode())
        try:codes.append(opener.open(request).status)
        except urllib.error.HTTPError as error:codes.append(error.code)
    assert 429 in codes and set(codes)<={401,429}
    before=fingerprint();stop(process);process=start();assert fingerprint()==before
    # Verify login works after restart and the limiter's new window.
    jar=http.cookiejar.CookieJar();opener=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))
    token=json.load(opener.open(base+'/api/csrf'))['token']
    request=urllib.request.Request(base+'/api/login',method='POST',headers={'Content-Type':'application/json','X-CSRF-TOKEN':token},data=json.dumps({'username':'dona.teste','password':'Jaq.Teste.2026!'}).encode())
    assert opener.open(request).status==200
    assert json.load(opener.open(base+'/api/data'))['clients']
    log.flush();assert 'fail:' not in (run/'server.log').read_text(encoding='utf-8'), 'Unexpected server error in log'
    summary={'passed':True,'results':str(run),'checks':['Login com tentativas repetidas é limitado','Reinício preserva exatamente o conteúdo das tabelas','Acesso funciona após reiniciar','Nenhum erro inesperado no servidor']}
    (run/'runtime.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8')
    (results/'latest.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8')
    print('PASS: suite complete. Results: '+str(run))
finally:stop(process);log.close()
