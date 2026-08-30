// BRegister.cpp - Stop MCP → Register bridge → Start MCP (all-in-one)
#include <windows.h>
#using <mscorlib.dll>
using namespace System;using namespace System::IO;using namespace System::Reflection;using namespace System::Threading;
ref class BReg{public:
static String^ LogPath(){return Path::Combine(Path::GetDirectoryName(Assembly::GetExecutingAssembly()->Location),"allurix_bootstrap.log");}
static void Run(){try{
Assembly^ bridgeAsm=nullptr;Type^ serverType=nullptr;Assembly^ mcsAsm=nullptr;
for each(Assembly^ a in AppDomain::CurrentDomain->GetAssemblies()){
 String^ n=a->GetName()->Name;
 if(n=="mcp_csharp_bridge"){bridgeAsm=a;serverType=a->GetType("MC.Mcp.McpServer");}
 if(n=="MCStudio")mcsAsm=a;
}
if(!serverType||!mcsAsm){L("ERR: assemblies not found");return;}

// Get McpServer via ManagerBase<McpServerHost>.Instance._server
Type^ hostType=mcsAsm->GetType("MCStudio.Modules.Mcp.McpServerHost");
PropertyInfo^ instProp=hostType->GetProperty("Instance",BindingFlags::Static|BindingFlags::Public|BindingFlags::FlattenHierarchy);
Object^ host=instProp->GetValue(nullptr);
if(!host){L("ERR: McpServerHost.Instance null");return;}
FieldInfo^ sf=hostType->GetField("_server",BindingFlags::Instance|BindingFlags::NonPublic);
Object^ srv=sf->GetValue(host);
if(!srv){L("ERR: _server null");return;}

// Check current status
PropertyInfo^ statusProp=serverType->GetProperty("Status");
String^ status=statusProp->GetValue(srv)->ToString();
L("McpServer status: "+status+", Port="+serverType->GetProperty("Port")->GetValue(srv));

// Stop if running
if(status!="Stopped"){
 L("Stopping MCP...");
 MethodInfo^ stopMethod=serverType->GetMethod("Stop");
 if(status!="Stopping"&&stopMethod)stopMethod->Invoke(srv,nullptr);
 for(int i=0;i<100&&status!="Stopped";i++){
  Thread::Sleep(100);
  status=statusProp->GetValue(srv)->ToString();
 }
 L("Status after stop: "+status);
 if(status!="Stopped"){L("ERR: MCP did not stop within 10 seconds");return;}
}

// Load and register bridge
String^ dir=Path::GetDirectoryName(Assembly::GetExecutingAssembly()->Location);
String^ dllPath=Path::Combine(dir,"allurix-mcs-bridge.dll");
bool registered=false;
try{
 if(!File::Exists(dllPath))L("ERR: "+dllPath+" not found");
 else{
  Assembly^ toolAsm=Assembly::LoadFrom(dllPath);
  L("Loaded: "+toolAsm->GetName()->Name);
  MethodInfo^ scan=serverType->GetMethod("ScanAndRegister",gcnew array<Type^>{Assembly::typeid});
  scan->Invoke(srv,gcnew array<Object^>{toolAsm});
  registered=true;
  L("Bridge registered!");
 }
}catch(Exception^ ex){L("EX:"+ex->GetType()->Name+":"+ex->Message);if(ex->InnerException)L("  I:"+ex->InnerException->Message);}

// Start MCP
L("Starting MCP...");
MethodInfo^ startMethod=serverType->GetMethod("Start");
if(startMethod)startMethod->Invoke(srv,nullptr);
for(int i=0;i<100&&status!="Running";i++){
 Thread::Sleep(100);
 status=statusProp->GetValue(srv)->ToString();
}
L("=== DONE! Status: "+status+" ===");
if(!registered)L("ERR: Bridge registration failed");
}catch(Exception^ ex){L("EX:"+ex->GetType()->Name+":"+ex->Message);if(ex->InnerException)L("  I:"+ex->InnerException->Message);}
}
static void L(String^ s){try{File::AppendAllText(LogPath(),DateTime::Now.ToString("HH:mm:ss")+" "+s+"\r\n");}catch(...){}}
};
static DWORD WINAPI BT(LPVOID){BReg::Run();return 0;}
#pragma managed(push, off)
BOOL APIENTRY DllMain(HMODULE h,DWORD r,LPVOID){if(r==DLL_PROCESS_ATTACH){DisableThreadLibraryCalls(h);CreateThread(NULL,0,BT,NULL,0,NULL);}return TRUE;}
#pragma managed(pop)
