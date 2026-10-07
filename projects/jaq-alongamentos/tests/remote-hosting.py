"""Tests remote hosting with a simulated HTTPS proxy and an isolated database."""
import http.cookies, json, os, socket, subprocess, tempfile, time, urllib.error, urllib.request
from pathlib import Path
# Python 3.7 predates SameSite support in its cookie parser.
http.cookies.Morsel._reserved.setdefault('samesite', 'SameSite')

root = Path(__file__).resolve().parents[1]
checks = []

with tempfile.TemporaryDirectory(prefix='jaq-remote-') as temporary:
    folder = Path(temporary)
    data = folder / 'data'
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0)); port = sock.getsockname()[1]
    base = 'http://127.0.0.1:' + str(port)
    server = None
    log = (folder / 'server.log').open('w', encoding='utf-8')
    env = os.environ.copy()
    env.update(JAQ_DATA_DIR=str(data), JAQ_REMOTE_HOSTING='false', Logging__LogLevel__Default='Error')

    def stop():
        global server
        if server and server.poll() is None:
            server.terminate(); server.wait(timeout=10)

    def call(path, body=None, headers=None):
        request = urllib.request.Request(base + path, data=json.dumps(body).encode() if body is not None else None,
                                        headers={'Content-Type': 'application/json', 'Host':'jaq.test', **(headers or {})})
        try:
            response = urllib.request.urlopen(request, timeout=3)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            raw = response.read()
            try: payload = json.loads(raw) if raw else None
            except ValueError: payload = raw.decode(errors='replace')
            return response.code, payload, response.headers

    def start(expect_failure=False):
        global server
        server = subprocess.Popen(['dotnet', str(root/'bin/Debug/net10.0/JaqAlongamentos.dll'), '--urls', base],
                                  cwd=root, env=env, stdout=log, stderr=log)
        for _ in range(100):
            if server.poll() is not None:
                assert expect_failure, 'Unexpected startup failure'
                return
            if not expect_failure:
                try:
                    call('/api/session', headers={'Host':'jaq.test','X-Forwarded-Proto':'https'})
                    return
                except OSError:
                    pass
            time.sleep(.1)
        stop(); raise AssertionError('Server did not reach expected startup state')

    def cookies(headers):
        result = http.cookies.SimpleCookie()
        for value in headers.get_all('Set-Cookie') or []:
            result.load(value)
        return result

    try:
        env.update(JAQ_REMOTE_HOSTING='true', AllowedHosts='jaq.test', JAQ_TRUSTED_PROXY='127.0.0.1')
        start(expect_failure=True)
        assert not (data/'jaq.db').exists()
        checks.append('Remote startup refuses a missing database before creating a new one')
        env['JAQ_REMOTE_HOSTING']='false'; start()
        stop(); env['JAQ_REMOTE_HOSTING']='true'; start(expect_failure=True)
        checks.append('Remote startup refuses a database with no configured owner')
        env['JAQ_REMOTE_HOSTING']='false'; start()
        code, csrf, headers = call('/api/csrf')
        cookie = '; '.join(m.key+'='+m.value for m in cookies(headers).values())
        assert call('/api/setup', {'name':'Dona teste','username':'dona.teste','password':'Jaq.Teste.2026!'},
                    {'Cookie':cookie,'X-CSRF-TOKEN':csrf['token']})[0] == 200
        stop()
        env['JAQ_REMOTE_HOSTING']='true'; start()
        assert call('/api/session', headers={'Host':'jaq.test'})[0] == 400
        checks.append('Remote mode rejects HTTP without the trusted HTTPS proxy')
        forwarded = {'Host':'jaq.test','X-Forwarded-Proto':'https','X-Forwarded-For':'198.51.100.10'}
        code, session, headers = call('/api/session', headers=forwarded)
        assert code == 200 and session['setupNeeded'] is False
        assert headers['Strict-Transport-Security'] == 'max-age=31536000'
        assert call('/api/session', headers={**forwarded,'Host':'other.test'})[0] == 400
        checks.append('Trusted HTTPS proxy works, enforces the configured domain, and sets HSTS')
        code, csrf, headers = call('/api/csrf', headers=forwarded)
        jar = cookies(headers)
        assert jar and all(m['secure'] for m in jar.values()), (code, [(m.key,dict(m)) for m in jar.values()], bool(headers.get_all('Set-Cookie')))
        cookie = '; '.join(m.key+'='+m.value for m in jar.values())
        code, _, headers = call('/api/login', {'username':'dona.teste','password':'Jaq.Teste.2026!'},
                               {**forwarded,'Cookie':cookie,'X-CSRF-TOKEN':csrf['token']})
        assert code == 200
        auth = cookies(headers)['Jaq.Session']
        assert auth['secure'] and auth['httponly'] and auth['samesite'] == 'strict'
        cookie += '; Jaq.Session='+auth.value
        assert call('/api/data', headers={**forwarded,'Cookie':cookie})[0] == 200
        _, csrf, _ = call('/api/csrf', headers={**forwarded,'Cookie':cookie})
        checks.append('Existing owner login, secure session and antiforgery cookies, and private data work')
        for _ in range(12):
            code, _, _ = call('/api/login', {'username':'nobody','password':'wrong'},
                             {**forwarded,'Cookie':cookie,'X-CSRF-TOKEN':csrf['token']})
        assert code == 429
        assert call('/api/login', {'username':'nobody','password':'wrong'},
                    {**forwarded,'X-Forwarded-For':'198.51.100.11','Cookie':cookie,'X-CSRF-TOKEN':csrf['token']})[0] == 401
        checks.append('Login rate limit uses the forwarded client IP instead of one shared proxy IP')
        stop(); env['JAQ_TRUSTED_PROXY']='192.0.2.1'; start()
        assert call('/api/session', headers=forwarded)[0] == 400
        checks.append('Headers from an untrusted proxy cannot claim HTTPS')
        stop(); env['AllowedHosts']='*'; start(expect_failure=True)
        checks.append('Remote startup refuses wildcard domains')
        print(json.dumps({'passed':True,'checks':checks}, ensure_ascii=False, indent=2))
    finally:
        stop(); log.close()
