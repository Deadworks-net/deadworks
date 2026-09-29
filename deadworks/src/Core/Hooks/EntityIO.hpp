#pragma once

#include <safetyhook.hpp>
#include <cstdint>

class CEntityInstance;

namespace deadworks {
namespace hooks {

struct EntityIOOutputDesc_t {
    const char *m_pName;
    uint32_t m_nFlags;
    uint32_t m_nOutputOffset;
};

struct CEntityIOOutput {
    void *vtable;
    void *m_pConnections;
    EntityIOOutputDesc_t *m_pDesc;
};

// Original signature: bool CEntityInstance::AcceptInput(
//     const char* pInputName, CEntityInstance* pActivator, CEntityInstance* pCaller,
//     variant_t* pValue)
// Since 6711 the output ID / unk arguments are gone; the wrapper builds the new input
// parameter objects itself before calling CEntityIdentity::AcceptInput.
inline safetyhook::InlineHook g_CEntityInstance_AcceptInput;
bool __fastcall Hook_CEntityInstance_AcceptInput(CEntityInstance *thisptr, const char *inputName,
                                                 CEntityInstance *activator, CEntityInstance *caller,
                                                 void *variantValue);

// Original signature: void CEntityIOOutput::FireOutputInternal(
//     CEntityInstance* pActivator, CEntityInstance* pCaller,
//     const void* pParams, float flDelay, void* unk, const CVariant* pValue)
// Since 6711 the fourth argument is the new output parameter container, not a CVariant.
// The CVariant moved to the last argument and is only passed by some callers (null otherwise).
inline safetyhook::InlineHook g_CEntityIOOutput_FireOutputInternal;
void __fastcall Hook_CEntityIOOutput_FireOutputInternal(CEntityIOOutput *pThis,
                                                        CEntityInstance *pActivator, CEntityInstance *pCaller,
                                                        const void *pParams, float delay,
                                                        void *unk, const void *pValue);

} // namespace hooks
} // namespace deadworks
