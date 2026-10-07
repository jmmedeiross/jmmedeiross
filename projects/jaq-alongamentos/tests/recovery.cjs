const {chromium}=require(process.env.JAQ_PLAYWRIGHT_PATH || 'playwright');
const fs=require('node:fs');
(async()=>{
 const browser=await chromium.launch({...(process.env.JAQ_BROWSER_EXECUTABLE?{executablePath:process.env.JAQ_BROWSER_EXECUTABLE}:{}),headless:true});
 const context=await browser.newContext({viewport:{width:1440,height:1000},timezoneId:'America/New_York'});
 const page=await context.newPage();const errors=[];const checks=[];
 page.on('pageerror',e=>errors.push(e.message));
 const login=async()=>{await page.goto(process.env.JAQ_TEST_URL || 'http://127.0.0.1:5188');await page.getByLabel('Usuário',{exact:true}).fill('dona.teste');await page.getByLabel('Senha',{exact:true}).fill('Jaq.Teste.2026!');await page.getByRole('button',{name:'Entrar',exact:true}).click();await page.getByRole('button',{name:'Clientes',exact:true}).waitFor();};
 await login();
 for(const width of [320,390,768,1440]){
  await page.setViewportSize({width,height:1000});
  for(const name of ['Visão geral','Agenda','Atendimentos','Clientes','Serviços','Equipe','Comissões','Retornos']){
   await page.getByRole('button',{name,exact:true}).click();
   if(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth))throw Error('Overflow '+width+' '+name);
  }
 }
 checks.push('Todas as oito telas em 320, 390, 768 e 1440 pixels sem rolagem horizontal externa');
 await page.getByRole('button',{name:'Clientes',exact:true}).click();
 await page.getByRole('button',{name:'＋ Nova cliente',exact:true}).click();
 await page.getByLabel('Nome da cliente',{exact:true}).fill('A'.repeat(100));
 await page.getByLabel('Preferências e observações').fill('B'.repeat(2000));
 await page.getByRole('button',{name:'Salvar',exact:true}).click();await page.locator('#modal').waitFor({state:'hidden'});
 await page.setViewportSize({width:320,height:1000});
 if(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth))throw Error('Long text overflow');
 checks.push('Nomes longos e observações no limite não quebram o celular');
 await page.getByRole('button',{name:'Editar ficha',exact:true}).click();
 // Simulate another professional saving the same record while this dialog is open.
 await page.evaluate(async()=>{
  const token=(await (await fetch('/api/csrf')).json()).token;
  const data=await (await fetch('/api/data')).json();
  const client=data.clients.find(c=>c.name==='A'.repeat(100));
  const result=await fetch('/api/clients/'+client.id,{method:'PUT',headers:{'Content-Type':'application/json','X-CSRF-TOKEN':token,'If-Match':'"'+client.revision+'"'},body:JSON.stringify({...client,preferences:'Alteração de outra pessoa'})});
  if(!result.ok)throw Error('Concurrent edit failed');
 });
 await page.getByLabel('Preferências e observações').fill('Minha alteração antiga');
 await page.getByRole('button',{name:'Salvar',exact:true}).click();
 await page.locator('#edit-form .error').getByText('Este registro mudou',{exact:false}).waitFor();
 await page.getByRole('button',{name:'Recarregar dados',exact:true}).click();await page.locator('#modal').waitFor({state:'hidden'});
 await page.getByText('Alteração de outra pessoa',{exact:true}).waitFor();
 checks.push('Formulário desatualizado mostra conflito e recarrega sem sobrescrever a outra alteração');
 await page.getByRole('button',{name:'Editar ficha',exact:true}).click();
 await page.getByLabel('Preferências e observações').fill('Salvo antes da falha de leitura');
 await page.route('**/api/data',route=>route.abort(),{times:1});
 await page.getByRole('button',{name:'Salvar',exact:true}).click();await page.locator('#modal').waitFor({state:'hidden'});
 await page.getByText('Registro salvo. Não foi possível atualizar a tela; recarregue os dados.',{exact:true}).waitFor();
 await page.reload();await page.getByRole('button',{name:'Clientes',exact:true}).waitFor();await page.getByRole('button',{name:'Clientes',exact:true}).click();
 await page.getByLabel('Buscar cliente').fill('A'.repeat(100));
 await page.locator('[data-client-search]:visible').getByRole('button',{name:'Abrir ficha'}).click();
 await page.getByText('Salvo antes da falha de leitura',{exact:true}).waitFor();
 checks.push('Falha de leitura após salvar é informada como registro salvo, sem erro oculto');
 await page.getByRole('button',{name:'Equipe',exact:true}).click();
 const owner=page.locator('.list-row').filter({hasText:'Dona Teste'});await owner.getByRole('button',{name:'Editar',exact:true}).click();
 if(await page.getByLabel('Perfil de acesso').locator('option').count()!==1)throw Error('Owner role selector');
 if(!await page.getByLabel('Usuário de acesso',{exact:true}).getAttribute('required').then(x=>x!==null))throw Error('Owner username required');
 await page.getByRole('button',{name:'Voltar',exact:true}).click();
 checks.push('Formulário da dona exige usuário e mantém o perfil administrativo');
 await page.getByRole('button',{name:'Agenda',exact:true}).click();
 await page.getByLabel('Dia',{exact:true}).fill('2032-11-05');
 await page.getByText('09:00',{exact:false}).first().waitFor();
 checks.push('Horários locais continuam corretos com navegador em outro fuso');
 await page.getByRole('button',{name:'Clientes',exact:true}).click();
 await page.getByLabel('Buscar cliente').fill('A'.repeat(100));
 await page.locator('[data-client-search]:visible').getByRole('button',{name:'Abrir ficha'}).click();
 await page.getByRole('button',{name:'Nova comanda',exact:true}).click();
 await page.getByRole('button',{name:'Salvar',exact:true}).click();await page.locator('#modal').waitFor({state:'hidden'});
 await page.getByRole('button',{name:'＋ Adicionar serviço',exact:true}).click();
 await page.getByLabel('Serviço',{exact:true}).selectOption({label:'Hidratação'});
 await page.getByLabel('Responsável',{exact:true}).selectOption({label:'Dona Teste'});
 const payout=page.getByLabel('Valor da responsável (R$)',{exact:true});
 if(await payout.inputValue()!=='')throw Error('Owner amount was assumed');
 await payout.fill('0');
 await page.getByLabel('Início (horário de Brasília)').fill('2035-10-06T09:00');
 await page.getByLabel('Duração (minutos)').fill('60');
 await page.getByRole('button',{name:'Salvar',exact:true}).click();await page.locator('#modal').waitFor({state:'hidden'});
 await page.getByRole('button',{name:'Editar',exact:true}).click();
 await page.getByLabel('Responsável',{exact:true}).selectOption({label:'Atendente 05'});
 if(await payout.inputValue()!=='')throw Error('Zero commission copied to attendant');
 await payout.fill('75');await page.getByRole('button',{name:'Salvar',exact:true}).click();await page.locator('#modal').waitFor({state:'hidden'});
 await page.getByText('Atendente recebe R$ 75,00',{exact:true}).waitFor();
 checks.push('Dona com valor zero e transferência para atendente exigem valor explícito na interface');

 await page.getByRole('button',{name:'Clientes',exact:true}).click();
 await page.getByRole('button',{name:'＋ Nova cliente',exact:true}).click();
 // Expire the authenticated session from the same browser while a form is open.
 await page.evaluate(async()=>{const token=(await (await fetch('/api/csrf')).json()).token;await fetch('/api/logout',{method:'POST',headers:{'X-CSRF-TOKEN':token}});});
 await page.getByLabel('Nome da cliente',{exact:true}).fill('Não deve salvar');await page.getByRole('button',{name:'Salvar',exact:true}).click();
 await page.getByRole('button',{name:'Entrar',exact:true}).waitFor();
 if(await page.locator('#shell').isVisible()||await page.locator('#modal').isVisible())throw Error('Expired session retained private screen');
 checks.push('Sessão expirada fecha formulário, limpa os dados e retorna ao acesso');
 if(errors.length)throw Error(errors.join('\n'));
 fs.writeFileSync(process.env.JAQ_RECOVERY_RESULTS || 'work/audit-browser-results.json',JSON.stringify(checks,null,2));
 await browser.close();console.log('PASS: '+checks.length+' grupos de interface e recuperação de falhas.');
})().catch(e=>{console.error(e);process.exit(1);});
