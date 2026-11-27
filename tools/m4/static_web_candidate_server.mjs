// Loopback-only static asset transport for the candidate guest. No route
// exposes evaluator state, reports, files outside the manifest or commands.
// Importing this module starts nothing. Host dispatch is not implemented.
import http from 'node:http';

export async function openCandidateAssetServer(assets){
  if(assets?.entryPath!=='/index.html'||typeof assets.resolve!=='function')
    throw Error('CANDIDATE_ASSET_SERVER_INVALID');
  let failed=false,closing;
  const server=http.createServer({maxHeaderSize:8192,requestTimeout:5000,
    headersTimeout:5000,keepAliveTimeout:1000},(req,res)=>{
    try{
      const result=assets.resolve(req.method,req.url);
      res.writeHead(result.status,{'Content-Type':result.contentType,
        'Cache-Control':'no-store','X-Content-Type-Options':'nosniff',
        'Content-Length':Buffer.byteLength(result.body),'Connection':'close'});
      res.end(result.body);
    }catch{
      failed=true;res.destroy();
    }
  });
  server.maxConnections=16;
  server.maxRequestsPerSocket=8;
  server.setTimeout(5000,socket=>socket.destroy());
  server.on('upgrade',(_req,socket)=>socket.destroy());
  server.on('connect',(_req,socket)=>socket.destroy());
  server.on('clientError',(_error,socket)=>socket.destroy());
  server.on('error',()=>{failed=true;});
  await new Promise((resolve,reject)=>{
    const onError=()=>reject(Error('CANDIDATE_ASSET_SERVER_UNAVAILABLE'));
    server.once('error',onError);
    server.listen(0,'127.0.0.1',()=>{server.off('error',onError);resolve();});
  });
  const origin='http://127.0.0.1:'+server.address().port;
  return Object.freeze({origin,close(){
    return closing??=new Promise((resolve,reject)=>{
      server.close(error=>{
        if(error||failed||server.listening)reject(Error('CANDIDATE_ASSET_SERVER_CLEANUP_UNCONFIRMED'));
        else resolve();
      });
      server.closeAllConnections();
    });
  }});
}
