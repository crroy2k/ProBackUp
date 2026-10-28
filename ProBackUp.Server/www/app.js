'use strict';
const $=id=>document.getElementById(id);let csrf='',page=0,admin=false;
const say=text=>{$('message').textContent=text};
async function api(path,body){const res=await fetch(path,{method:body===undefined?'GET':'POST',credentials:'same-origin',headers:body===undefined?{}:{'Content-Type':'application/json','X-CSRF-TOKEN':csrf},body:body===undefined?undefined:JSON.stringify(body)});if(!res.ok){if(res.status===401){showLogin();throw Error('Sesión no válida o credenciales no autorizadas.')}throw Error(res.status===429?'Demasiados intentos. Espere antes de volver a intentar.':res.status===403?'Acceso denegado.':res.status===503?'Servicio no disponible. Llame a T.I. Ext. 233.':'No se guardó. Revise los campos y la conexión al directorio.')}const text=await res.text();return text?JSON.parse(text):null}
async function token(){csrf=(await api('/api/csrf')).token}
function showLogin(){for(const id of ['remoteBackup','editClient','details'])if($(id).open)$(id).close();$('login').hidden=false;$('dashboard').hidden=true;$('logout').hidden=true;$('identity').textContent='';$('agentToken').value='';$('enrollment').hidden=true}
function node(tag,text,cls){const e=document.createElement(tag);e.textContent=text;if(cls)e.className=cls;return e}
function bytes(n){if(!n)return '0 B';const units=['B','KB','MB','GB','TB'];let i=0;while(n>=1024&&i<4){n/=1024;i++}return n.toLocaleString('es-CR',{maximumFractionDigits:2})+' '+units[i]}
function time(value){return value?new Intl.DateTimeFormat('es-CR',{timeZone:'America/Costa_Rica',dateStyle:'short',timeStyle:'medium'}).format(new Date(value)):'Sin contacto'}
const typeName={FullZip:'Completo ZIP',FullFolder:'Completo en carpeta',Incremental:'Incremental'};
function guarded(fn){return async e=>{if(e)e.preventDefault();say('');try{await fn()}catch(error){say(error.message)}}}
function selectMain(name){for(const [button,panel] of [['showReports','reportsPanel'],['showAdmin','adminPanel']]){const selected=button===name;$(button).classList.toggle('active',selected);$(button).setAttribute('aria-pressed',String(selected));$(panel).hidden=!selected}}
function selectAdmin(name){for(const [button,panel] of [['showClients','clientsPanel'],['showDirectory','directoryPanel'],['showAudit','auditPanel']]){const selected=button===name;$(button).classList.toggle('active',selected);$(button).setAttribute('aria-pressed',String(selected));$(panel).hidden=!selected}if(name!=='showClients'){$('agentToken').value='';$('enrollment').hidden=true}}
async function loadAudit(){const audit=await api('/api/admin/audit');$('audit').replaceChildren(...(audit.length?audit.map(a=>node('p',time(a.at)+' · '+a.actor+' · '+a.action)):[node('p','No hay eventos de auditoría.','muted')]))}
async function enter(){const me=await api('/api/me');admin=me.admin;$('identity').textContent=me.user;$('login').hidden=true;$('dashboard').hidden=false;$('logout').hidden=false;$('showAdmin').hidden=!admin;await token();selectMain('showReports');selectAdmin('showClients');await loadReports()}
function backupHealth(a,now=Date.now()){
if(a.revoked)return {text:'Clave revocada',css:'error'};
const last=Date.parse(a.lastBackup);
if(!Number.isFinite(last))return {text:'Sin respaldos exitosos',css:'error'};
const age=Math.max(0,now-last);
return age>=3*86400000?{text:'3 días o más sin respaldo',css:'error'}:age>=86400000?{text:'1 día o más sin respaldo',css:'warning'}:{text:'Respaldo al día',css:'ok'};
}
async function machines(){
const data=await api('/api/agents'),selected=$('agent').value;
$('agent').replaceChildren(node('option','Todos'));$('agent').firstChild.value='';$('machines').replaceChildren();
for(const a of data){
const option=node('option',a.name);option.value=a.id;$('agent').append(option);
const health=backupHealth(a),row=node('tr','');
row.append(node('td',a.name,'machine-name'),node('td',a.machine||'Aún no registrado','muted'),node('td',health.text,health.css),node('td',a.lastBackup?time(a.lastBackup):'Sin respaldo registrado'),node('td',time(a.seen),'muted'));
$('machines').append(row);
}
if(!data.length){const row=node('tr',''),cell=node('td','No hay equipos registrados.','muted');cell.colSpan=5;row.append(cell);$('machines').append(row)}
$('agent').value=selected;return data
}
function reportQuery(paged=false){const query=new URLSearchParams();if(paged)query.set('page',page);for(const field of ['agent','type','status','destination','user'])if($(field).value)query.set(field,$(field).value);if($('from').value)query.set('from',$('from').value+'T00:00:00-06:00');if($('to').value)query.set('to',new Date(Date.parse($('to').value+'T00:00:00-06:00')+86400000).toISOString());return query}
async function loadReports(){await machines();const query=reportQuery(true);const data=await api('/api/reports?'+query);$('rows').replaceChildren();for(const row of data.rows){const r=row.report,tr=node('tr','');function cell(...parts){const td=node('td','');for(const part of parts)td.append(node('p',part));tr.append(td);return td}cell(row.name,r.user||'Usuario no informado');cell(r.jobName,typeName[r.format]||r.format);cell(time(r.started),Math.max(0,Math.round((Date.parse(r.finished)-Date.parse(r.started))/1000))+' segundos');cell(r.destination);const state=cell((r.status==='Correcto'?'✓ ':r.status==='Error'?'✕ ':'')+r.status);state.className=r.status==='Correcto'?'ok':r.status==='Error'?'error':'';cell(bytes(r.bytes));cell(bytes(r.copiedBytes));const td=cell(),b=node('button','Ver detalle');b.onclick=()=>{$('detailText').textContent=`Equipo: ${row.name}\nUsuario: ${r.user}\nTarea: ${r.jobName}\nTipo: ${typeName[r.format]}\nInicio: ${time(r.started)}\nFin: ${time(r.finished)}\nDestino: ${r.destination}\nRespaldo: ${r.artifact}\nArchivos: ${r.files}\nVersión: ${bytes(r.bytes)}\nDatos nuevos: ${bytes(r.copiedBytes)}\nResultado: ${r.status}\n${r.error||''}`;$('details').showModal()};td.append(b);$('rows').append(tr)}$('summary').textContent=data.rows.length+' ejecuciones en esta página · Actualizado '+time(new Date().toISOString());$('page').textContent='Página '+(page+1);$('previous').disabled=page===0;$('next').disabled=!data.more}
function addAuthorizedUser(user={upn:'',role:'Reader'}){
const row=node('tr',''),nameCell=node('td',''),roleCell=node('td',''),actionCell=node('td','');
const input=node('input','');input.type='text';input.required=true;input.maxLength=100;input.placeholder='usuario';input.value=user.upn.split('@')[0];input.setAttribute('aria-label','Usuario autorizado');input.autocomplete='off';input.pattern='[^@\\s]+';row.originalUpn=user.upn;row.userInput=input;nameCell.append(input);
const roles=node('select','');roles.setAttribute('aria-label','Permisos del usuario');for(const [value,label] of [['Reader','Consulta'],['Admin','Administrador']]){const option=node('option',label);option.value=value;roles.append(option)}roles.value=user.role;row.roleInput=roles;roleCell.append(roles);
const remove=node('button','Quitar','danger');remove.type='button';remove.setAttribute('aria-label','Quitar usuario de la lista');remove.onclick=()=>row.remove();actionCell.append(remove);row.append(nameCell,roleCell,actionCell);$('authorizedUsers').append(row);return input;
}
$('addUser').onclick=()=>addAuthorizedUser().focus();
async function directory(){const settings=await api('/api/admin/ldap');$('ldapHost').value=settings.host;$('ldapPort').value=settings.port;$('baseDn').value=settings.baseDn;$('bindDn').value=settings.bindDn;$('bindPassword').value='';$('userDomain').textContent=settings.domain;$('authorizedUsers').replaceChildren();for(const user of settings.users)addAuthorizedUser(user);selectAdmin('showDirectory')}
async function administration(){await registered();selectAdmin('showClients');selectMain('showAdmin')}
let editingClient='';
async function registered(){
const agents=await api('/api/agents');$('registered').replaceChildren();
if(!agents.length){const row=node('tr',''),cell=node('td','No hay clientes registrados.');cell.colSpan=6;row.append(cell);$('registered').append(row)}
for(const a of agents){
const row=node('tr','');
function cell(text,css){const td=node('td',text,css);row.append(td);return td}
cell(a.name);cell(a.machine||'Aún no registrado');const health=backupHealth(a);cell(health.text,health.css);cell(a.lastBackup?time(a.lastBackup):'Sin respaldo registrado');cell(time(a.seen));
const actions=cell('');actions.className='client-actions';
if(!a.revoked){const run=node('button','Respaldar','primary');run.onclick=guarded(()=>openRemote(a));actions.append(run)}
const edit=node('button','Editar');edit.onclick=()=>{editingClient=a.id;$('editClientName').value=a.name;$('editClient').showModal()};actions.append(edit);
if(!a.revoked){const revoke=node('button','Revocar clave');revoke.onclick=guarded(async()=>{if(confirm('¿Revocar el envío de reportes de '+a.name+'?')){await api('/api/admin/agents/'+a.id+'/revoke',{});await registered()}});actions.append(revoke)}
const remove=node('button','Eliminar','danger');remove.onclick=guarded(async()=>{if(confirm('¿Eliminar el cliente «'+a.name+'» y sus reportes del servidor? Su clave dejará de funcionar. Los archivos de respaldo en el destino no se eliminan. Esta acción no se puede deshacer.')){await api('/api/admin/agents/'+a.id+'/delete',{});await registered();say('Cliente y reportes centrales eliminados.')}});actions.append(remove);$('registered').append(row);
}}
let remoteAgent='';
async function remoteRequests(){
const id=remoteAgent,data=await api('/api/admin/agents/'+id+'/commands');if(remoteAgent!==id||!$('remoteBackup').open)return;
$('remoteCommands').replaceChildren();
for(const c of data){const row=node('tr','');for(const text of [c.name,time(c.created),c.state,c.message])row.append(node('td',text));row.children[2].className=c.state==='Correcto'?'ok':['Error','Rechazado','Caducado','Cancelado'].includes(c.state)?'error':'warning';$('remoteCommands').append(row)}
if(!data.length){const row=node('tr',''),cell=node('td','Aún no hay solicitudes.');cell.colSpan=4;row.append(cell);$('remoteCommands').append(row)}
}
async function remoteJobs(){
const jobs=await api('/api/admin/agents/'+remoteAgent+'/jobs');$('remoteJob').replaceChildren();
for(const job of jobs){const option=node('option',job.name+' · '+(typeName[job.format]||job.format)+' · '+job.destination);option.value=job.id;$('remoteJob').append(option)}
$('sendBackup').disabled=!jobs.length;
$('remoteMessage').textContent=jobs.length?'Se ejecutará con el destino y las credenciales guardados en el cliente.':'El cliente aún no ha enviado sus tareas. Instala ProBackUp 3.3, configura el servidor central y comprueba que su servicio esté activo.';
await remoteRequests();
}
async function openRemote(agent){remoteAgent=agent.id;$('remoteTitle').textContent='Respaldar · '+agent.name;$('remoteJob').replaceChildren();$('remoteCommands').replaceChildren();$('sendBackup').disabled=true;$('remoteMessage').textContent='Consultando tareas…';$('remoteBackup').showModal();await remoteJobs()}
$('remoteBackupForm').onsubmit=guarded(async()=>{
if(!confirm('¿Ejecutar ahora la tarea seleccionada en este cliente?'))return;
$('sendBackup').disabled=true;
try{await api('/api/admin/agents/'+remoteAgent+'/backup',{jobId:$('remoteJob').value});$('remoteMessage').textContent='Solicitud registrada. El cliente la ejecutará cuando su usuario tenga sesión iniciada y esté libre. Las solicitudes repetidas de la misma tarea se agrupan mientras esté pendiente o en curso.';await remoteRequests()}finally{$('sendBackup').disabled=!$('remoteJob').options.length}
});
$('refreshRemote').onclick=guarded(remoteJobs);$('closeRemote').onclick=()=>$('remoteBackup').close();
setInterval(()=>{if($('remoteBackup').open)remoteRequests().catch(error=>{$('remoteMessage').textContent=error.message})},5000);
$('editClientForm').onsubmit=guarded(async()=>{await api('/api/admin/agents/'+editingClient+'/edit',{name:$('editClientName').value.trim()});$('editClient').close();await registered();say('Nombre del cliente actualizado.')});
$('cancelEditClient').onclick=()=>$('editClient').close();
$('loginForm').onsubmit=guarded(async()=>{try{await api('/api/login',{upn:$('upn').value.trim(),password:$('password').value});await enter()}finally{$('password').value=''}});
$('logout').onclick=guarded(async()=>{await api('/api/logout',{});showLogin();await token()});
$('exportReports').onclick=guarded(async()=>{
const button=$('exportReports');button.disabled=true;button.textContent='Exportando…';
try{
const res=await fetch('/api/reports/export.xlsx?'+reportQuery(),{credentials:'same-origin'});
if(res.status===401){showLogin();throw Error('Inicia sesión de nuevo para exportar.')}if(!res.ok){if(res.status===429)throw Error('Hay otra exportación en curso. Intenta de nuevo en unos segundos.');let message='No se pudo exportar el historial.';try{const detail=await res.json();if(detail.error)message=detail.error}catch{}throw Error(message)}
const url=URL.createObjectURL(await res.blob()),link=document.createElement('a');link.href=url;link.download=(res.headers.get('Content-Disposition')||'').match(/filename="?([^";]+)/i)?.[1]||'ProBackUp-respaldos.xlsx';document.body.append(link);link.click();link.remove();setTimeout(()=>URL.revokeObjectURL(url),30000);say('Excel generado con todas las coincidencias de los filtros.');
}finally{button.disabled=false;button.textContent='Exportar a Excel'}
});
$('filters').onsubmit=guarded(async()=>{page=0;await loadReports()});$('previous').onclick=guarded(async()=>{page--;await loadReports()});$('next').onclick=guarded(async()=>{page++;await loadReports()});
$('showAdmin').onclick=guarded(administration);$('showReports').onclick=guarded(async()=>{await loadReports();selectMain('showReports')});
$('showClients').onclick=guarded(async()=>{await registered();selectAdmin('showClients')});$('showDirectory').onclick=guarded(directory);$('showAudit').onclick=guarded(async()=>{await loadAudit();selectAdmin('showAudit')});$('refreshAudit').onclick=guarded(loadAudit);
$('ldapForm').onsubmit=guarded(async()=>{const users=[...$('authorizedUsers').children].map(row=>{const name=row.userInput.value.trim();if(!name||/[@\s\\/]/u.test(name))throw Error('Escribe solo el usuario, sin dominio ni espacios.');return {upn:row.originalUpn&&row.originalUpn.split('@')[0].toLowerCase()===name.toLowerCase()?row.originalUpn:name,role:row.roleInput.value}});try{await api('/api/admin/ldap',{host:$('ldapHost').value,port:Number($('ldapPort').value),baseDn:$('baseDn').value,bindDn:$('bindDn').value,password:$('bindPassword').value,users});showLogin();await token();say('LDAP validado y guardado. Inicie sesión de nuevo.')}finally{$('bindPassword').value=''}});
$('registerForm').onsubmit=guarded(async()=>{const created=await api('/api/admin/agents',{name:$('agentName').value});$('agentToken').value=created.token;$('enrollment').hidden=false;await registered()});$('hideToken').onclick=()=>{$('agentToken').value='';$('enrollment').hidden=true};
(async()=>{try{await token();try{await enter()}catch{showLogin()}}catch(error){say(error.message)}})();

setInterval(()=>{if(!$('dashboard').hidden&&!$('reportsPanel').hidden)machines().catch(error=>say(error.message))},60000);
