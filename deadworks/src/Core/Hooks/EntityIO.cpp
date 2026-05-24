#include "EntityIO.hpp"

#include "../Deadworks.hpp"
#include <entity2/entityinstance.h>

namespace deadworks {
namespace hooks {

bool __fastcall Hook_CEntityInstance_AcceptInput(CEntityInstance *thisptr, const char *inputName,
                                                 CEntityInstance *activator, CEntityInstance *caller,
                                                 void *variantValue, int outputID, void *unk) {
    const char *className = thisptr ? thisptr->GetClassname() : "";

    int result = g_Deadworks.OnEntityAcceptInputPre(className, inputName, thisptr, activator, caller, variantValue);
    if (result >= 1) {
        return true;
    }

    bool ret = g_CEntityInstance_AcceptInput.fastcall<bool>(thisptr, inputName, activator, caller, variantValue, outputID, unk);

    g_Deadworks.OnEntityAcceptInputPost(className, inputName, thisptr, activator, caller, variantValue);
    return ret;
}

void __fastcall Hook_CEntityIOOutput_FireOutputInternal(CEntityIOOutput *pThis,
                                                        CEntityInstance *pActivator, CEntityInstance *pCaller,
                                                        const void *pValue, float delay,
                                                        void *unk1, void *unk2) {
    const char *callerClass = pCaller ? pCaller->GetClassname() : "";
    const char *outputName = (pThis && pThis->m_pDesc) ? pThis->m_pDesc->m_pName : "";

    int result = g_Deadworks.OnEntityFireOutputPre(callerClass, outputName, pActivator, pCaller, pValue, delay);
    if (result >= 1) {
        return;
    }

    g_CEntityIOOutput_FireOutputInternal.fastcall<void>(pThis, pActivator, pCaller, pValue, delay, unk1, unk2);

    g_Deadworks.OnEntityFireOutputPost(callerClass, outputName, pActivator, pCaller, pValue, delay);
}

} // namespace hooks
} // namespace deadworks
