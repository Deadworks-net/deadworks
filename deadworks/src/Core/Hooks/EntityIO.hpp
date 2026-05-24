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
//     variant_t* pValue, int nOutputID, void* unk)
inline safetyhook::InlineHook g_CEntityInstance_AcceptInput;
bool __fastcall Hook_CEntityInstance_AcceptInput(CEntityInstance *thisptr, const char *inputName,
                                                 CEntityInstance *activator, CEntityInstance *caller,
                                                 void *variantValue, int outputID, void *unk);

// Original signature: void CEntityIOOutput::FireOutputInternal(
//     CEntityInstance* pActivator, CEntityInstance* pCaller,
//     const CVariant* value, float flDelay, void* unk1, void* unk2)
inline safetyhook::InlineHook g_CEntityIOOutput_FireOutputInternal;
void __fastcall Hook_CEntityIOOutput_FireOutputInternal(CEntityIOOutput *pThis,
                                                        CEntityInstance *pActivator, CEntityInstance *pCaller,
                                                        const void *pValue, float delay,
                                                        void *unk1, void *unk2);

} // namespace hooks
} // namespace deadworks
