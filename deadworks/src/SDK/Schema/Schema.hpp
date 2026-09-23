#pragma once

#include <string_view>
#include <type_traits>

void EntityNetworkStateChanged(uintptr_t pEntity, uint32_t nOffset);
void ChainNetworkStateChanged(uintptr_t pNetworkVarChainer, uint32_t nOffset);
void NetworkVarStateChanged(uintptr_t pNetworkVar, uint32_t nOffset, uint32_t nNetworkStateChangedOffset);

struct SchemaKey {
    int32_t Offset;
    bool Networked;
};

namespace schema {
int16_t FindChainOffset(const char *className, uint32_t classNameHash);
SchemaKey GetOffset(const char *className, uint32_t classKey, const char *memberName, uint32_t memberKey);
int GetClassSize(const char *className);
// True when className is baseClassName or inherits from it anywhere in its schema base-class tree.
bool IsDerivedFrom(const char *className, const char *baseClassName);
} // namespace schema

constexpr uint32_t val_32_const = 0x811c9dc5;
constexpr uint32_t prime_32_const = 0x1000193;
constexpr uint64_t val_64_const = 0xcbf29ce484222325;
constexpr uint64_t prime_64_const = 0x100000001b3;

inline constexpr uint32_t hash_32_fnv1a_const(const char *const str, const uint32_t value = val_32_const) noexcept {
    return (str[0] == '\0') ? value : hash_32_fnv1a_const(&str[1], (value ^ uint32_t(str[0])) * prime_32_const);
}

inline constexpr uint64_t hash_64_fnv1a_const(const char *const str, const uint64_t value = val_64_const) noexcept {
    return (str[0] == '\0') ? value : hash_64_fnv1a_const(&str[1], (value ^ uint64_t(str[0])) * prime_64_const);
}

#define DECLARE_SCHEMA_CLASS_BASE(className, offset)                             \
public:                                                                          \
    const char *GetClassName() const { return m_className; }                     \
                                                                                 \
private:                                                                         \
    using ThisClass = className;                                                 \
    static constexpr const char *m_className = #className;                       \
    static constexpr uint32_t m_classNameHash = hash_32_fnv1a_const(#className); \
    static constexpr int m_networkStateChangedOffset = offset;                   \
                                                                                 \
public:

#define DECLARE_SCHEMA_CLASS(className) DECLARE_SCHEMA_CLASS_BASE(className, 0)

// Use when the C++ class name differs from the engine schema name -
// e.g., an accessor overlay class that queries schema as a different type.
#define DECLARE_SCHEMA_CLASS_ALIAS(cppClass, schemaName)                          \
public:                                                                           \
    const char *GetClassName() const { return m_className; }                      \
                                                                                  \
private:                                                                          \
    using ThisClass = cppClass;                                                   \
    static constexpr const char *m_className = #schemaName;                       \
    static constexpr uint32_t m_classNameHash = hash_32_fnv1a_const(#schemaName); \
    static constexpr int m_networkStateChangedOffset = 0;                         \
                                                                                  \
public:

#define SCHEMA_FIELD_OFFSET(type, varName, extraOffset)                                                              \
    class varName##_proxy {                                                                                          \
    public:                                                                                                          \
        std::add_lvalue_reference_t<type> Get() {                                                                    \
            static const auto schemaKey = schema::GetOffset(m_className, m_classNameHash, #varName, m_varNameHash);  \
            static const auto proxyOffsetInClass = offsetof(ThisClass, varName);                                     \
            uintptr_t pThisClass = reinterpret_cast<uintptr_t>(this) - proxyOffsetInClass;                           \
            return *reinterpret_cast<std::add_pointer_t<type>>(pThisClass + schemaKey.Offset);                       \
        }                                                                                                            \
                                                                                                                     \
        void Set(type &value) {                                                                                      \
            static const auto schemaKey = schema::GetOffset(m_className, m_classNameHash, #varName, m_varNameHash);  \
            static const auto proxyOffsetInClass = offsetof(ThisClass, varName);                                     \
                                                                                                                     \
            uintptr_t pThisClass = reinterpret_cast<uintptr_t>(this) - proxyOffsetInClass;                           \
            *reinterpret_cast<std::add_pointer_t<type>>(pThisClass + schemaKey.Offset + extraOffset) = value;        \
            NetworkStateChanged();                                                                                   \
        }                                                                                                            \
                                                                                                                     \
        void NetworkStateChanged() {                                                                                 \
            static const auto schemaKey = schema::GetOffset(m_className, m_classNameHash, #varName, m_varNameHash);  \
            static const auto chain = schema::FindChainOffset(m_className, m_classNameHash);                         \
            static const auto proxyOffsetInClass = offsetof(ThisClass, varName);                                     \
                                                                                                                     \
            uintptr_t pThisClass = reinterpret_cast<uintptr_t>(this) - proxyOffsetInClass;                           \
                                                                                                                     \
            if (chain != 0 && schemaKey.Networked) {                                                                 \
                ChainNetworkStateChanged(pThisClass + chain, schemaKey.Offset + extraOffset);                        \
            } else if (schemaKey.Networked) {                                                                        \
                if (!m_networkStateChangedOffset)                                                                    \
                    EntityNetworkStateChanged(pThisClass, schemaKey.Offset + extraOffset);                           \
                else                                                                                                 \
                    NetworkVarStateChanged(pThisClass, schemaKey.Offset + extraOffset, m_networkStateChangedOffset); \
            }                                                                                                        \
        }                                                                                                            \
                                                                                                                     \
        operator std::add_lvalue_reference_t<type>() {                                                               \
            return Get();                                                                                            \
        }                                                                                                            \
                                                                                                                     \
        std::add_lvalue_reference_t<type> operator->() {                                                             \
            return Get();                                                                                            \
        }                                                                                                            \
                                                                                                                     \
        std::add_lvalue_reference_t<type> operator=(type value) {                                                    \
            Set(value);                                                                                              \
            return Get();                                                                                            \
        }                                                                                                            \
                                                                                                                     \
    private:                                                                                                         \
        varName##_proxy(const varName##_proxy &) = delete;                                                           \
        static constexpr auto m_varNameHash = hash_32_fnv1a_const(#varName);                                         \
    } varName;

#define SCHEMA_FIELD(type, varName) SCHEMA_FIELD_OFFSET(type, varName, 0)

#define SCHEMA_FIELD_POINTER_OFFSET(type, varName, extraOffset)                                                      \
    class varName##_proxy {                                                                                          \
    public:                                                                                                          \
        type *Get() {                                                                                                \
            static const auto schemaKey = schema::GetOffset(m_className, m_classNameHash, #varName, m_varNameHash);  \
            static const auto proxyOffsetInClass = offsetof(ThisClass, varName);                                     \
            uintptr_t pThisClass = reinterpret_cast<uintptr_t>(this) - proxyOffsetInClass;                           \
            return reinterpret_cast<type *>(pThisClass + schemaKey.Offset + extraOffset);                            \
        }                                                                                                            \
        void NetworkStateChanged() {                                                                                 \
            static const auto schemaKey = schema::GetOffset(m_className, m_classNameHash, #varName, m_varNameHash);  \
            static const auto chain = schema::FindChainOffset(m_className, m_classNameHash);                         \
            static const auto proxyOffsetInClass = offsetof(ThisClass, varName);                                     \
                                                                                                                     \
            uintptr_t pThisClass = reinterpret_cast<uintptr_t>(this) - proxyOffsetInClass;                           \
                                                                                                                     \
            if (chain != 0 && schemaKey.Networked) {                                                                 \
                ChainNetworkStateChanged(pThisClass + chain, schemaKey.Offset + extraOffset);                        \
            } else if (schemaKey.Networked) {                                                                        \
                if (!m_networkStateChangedOffset)                                                                    \
                    EntityNetworkStateChanged(pThisClass, schemaKey.Offset + extraOffset);                           \
                else                                                                                                 \
                    NetworkVarStateChanged(pThisClass, schemaKey.Offset + extraOffset, m_networkStateChangedOffset); \
            }                                                                                                        \
        }                                                                                                            \
        operator type *() { return Get(); }                                                                          \
        type *operator()() { return Get(); }                                                                         \
        type *operator->() { return Get(); }                                                                         \
                                                                                                                     \
    private:                                                                                                         \
        varName##_proxy(const varName##_proxy &) = delete;                                                           \
        static constexpr auto m_varNameHash = hash_32_fnv1a_const(#varName);                                         \
    } varName;

#define SCHEMA_FIELD_POINTER(type, varName) SCHEMA_FIELD_POINTER_OFFSET(type, varName, 0)
