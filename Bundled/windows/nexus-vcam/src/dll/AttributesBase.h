// Implements the IMFAttributes portion of any interface deriving from it
// (or IMFAttributes itself) by delegating to a private store created with
// MFCreateAttributes. Mirrors the CBaseAttributes pattern used by the
// Microsoft / smourier virtual camera samples.

#pragma once

#include "Framework.h"

template <typename Base>
class AttributesBase : public Base
{
protected:
    AttributesBase() = default;

    HRESULT InitializeAttributes() noexcept
    {
        return MFCreateAttributes(_attributes.Put(), 0);
    }

    void ResetAttributes() noexcept
    {
        _attributes.Reset();
    }

    ComPtr<IMFAttributes> _attributes;

public:
    // IMFAttributes
    STDMETHODIMP GetItem(REFGUID guidKey, PROPVARIANT* pValue) override
    {
        NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_attributes);
        return _attributes->GetItem(guidKey, pValue);
    }

    STDMETHODIMP GetItemType(REFGUID guidKey, MF_ATTRIBUTE_TYPE* pType) override
    {
        NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_attributes);
        return _attributes->GetItemType(guidKey, pType);
    }

    STDMETHODIMP CompareItem(REFGUID guidKey, REFPROPVARIANT value, BOOL* pbResult) override
    {
        NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_attributes);
        return _attributes->CompareItem(guidKey, value, pbResult);
    }

    STDMETHODIMP Compare(IMFAttributes* pTheirs, MF_ATTRIBUTES_MATCH_TYPE matchType, BOOL* pbResult) override
    {
        NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_attributes);
        return _attributes->Compare(pTheirs, matchType, pbResult);
    }

    STDMETHODIMP GetUINT32(REFGUID guidKey, UINT32* punValue) override
    {
        NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_attributes);
        return _attributes->GetUINT32(guidKey, punValue);
    }

    STDMETHODIMP GetUINT64(REFGUID guidKey, UINT64* punValue) override
    {
        NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_attributes);
        return _attributes->GetUINT64(guidKey, punValue);
    }

    STDMETHODIMP GetDouble(REFGUID guidKey, double* pfValue) override
    {
        NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_attributes);
        return _attributes->GetDouble(guidKey, pfValue);
    }

    STDMETHODIMP GetGUID(REFGUID guidKey, GUID* pguidValue) override
    {
        NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_attributes);
        return _attributes->GetGUID(guidKey, pguidValue);
    }

    STDMETHODIMP GetStringLength(REFGUID guidKey, UINT32* pcchLength) override
    {
        NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_attributes);
        return _attributes->GetStringLength(guidKey, pcchLength);
    }

    STDMETHODIMP GetString(REFGUID guidKey, LPWSTR pwszValue, UINT32 cchBufSize, UINT32* pcchLength) override
    {
        NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_attributes);
        return _attributes->GetString(guidKey, pwszValue, cchBufSize, pcchLength);
    }

    STDMETHODIMP GetAllocatedString(REFGUID guidKey, LPWSTR* ppwszValue, UINT32* pcchLength) override
    {
        NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_attributes);
        return _attributes->GetAllocatedString(guidKey, ppwszValue, pcchLength);
    }

    STDMETHODIMP GetBlobSize(REFGUID guidKey, UINT32* pcbBlobSize) override
    {
        NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_attributes);
        return _attributes->GetBlobSize(guidKey, pcbBlobSize);
    }

    STDMETHODIMP GetBlob(REFGUID guidKey, UINT8* pBuf, UINT32 cbBufSize, UINT32* pcbBlobSize) override
    {
        NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_attributes);
        return _attributes->GetBlob(guidKey, pBuf, cbBufSize, pcbBlobSize);
    }

    STDMETHODIMP GetAllocatedBlob(REFGUID guidKey, UINT8** ppBuf, UINT32* pcbSize) override
    {
        NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_attributes);
        return _attributes->GetAllocatedBlob(guidKey, ppBuf, pcbSize);
    }

    STDMETHODIMP GetUnknown(REFGUID guidKey, REFIID riid, LPVOID* ppv) override
    {
        NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_attributes);
        return _attributes->GetUnknown(guidKey, riid, ppv);
    }

    STDMETHODIMP SetItem(REFGUID guidKey, REFPROPVARIANT value) override
    {
        NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_attributes);
        return _attributes->SetItem(guidKey, value);
    }

    STDMETHODIMP DeleteItem(REFGUID guidKey) override
    {
        NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_attributes);
        return _attributes->DeleteItem(guidKey);
    }

    STDMETHODIMP DeleteAllItems() override
    {
        NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_attributes);
        return _attributes->DeleteAllItems();
    }

    STDMETHODIMP SetUINT32(REFGUID guidKey, UINT32 unValue) override
    {
        NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_attributes);
        return _attributes->SetUINT32(guidKey, unValue);
    }

    STDMETHODIMP SetUINT64(REFGUID guidKey, UINT64 unValue) override
    {
        NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_attributes);
        return _attributes->SetUINT64(guidKey, unValue);
    }

    STDMETHODIMP SetDouble(REFGUID guidKey, double fValue) override
    {
        NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_attributes);
        return _attributes->SetDouble(guidKey, fValue);
    }

    STDMETHODIMP SetGUID(REFGUID guidKey, REFGUID guidValue) override
    {
        NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_attributes);
        return _attributes->SetGUID(guidKey, guidValue);
    }

    STDMETHODIMP SetString(REFGUID guidKey, LPCWSTR wszValue) override
    {
        NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_attributes);
        return _attributes->SetString(guidKey, wszValue);
    }

    STDMETHODIMP SetBlob(REFGUID guidKey, const UINT8* pBuf, UINT32 cbBufSize) override
    {
        NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_attributes);
        return _attributes->SetBlob(guidKey, pBuf, cbBufSize);
    }

    STDMETHODIMP SetUnknown(REFGUID guidKey, IUnknown* pUnknown) override
    {
        NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_attributes);
        return _attributes->SetUnknown(guidKey, pUnknown);
    }

    STDMETHODIMP LockStore() override
    {
        NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_attributes);
        return _attributes->LockStore();
    }

    STDMETHODIMP UnlockStore() override
    {
        NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_attributes);
        return _attributes->UnlockStore();
    }

    STDMETHODIMP GetCount(UINT32* pcItems) override
    {
        NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_attributes);
        return _attributes->GetCount(pcItems);
    }

    STDMETHODIMP GetItemByIndex(UINT32 unIndex, GUID* pguidKey, PROPVARIANT* pValue) override
    {
        NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_attributes);
        return _attributes->GetItemByIndex(unIndex, pguidKey, pValue);
    }

    STDMETHODIMP CopyAllItems(IMFAttributes* pDest) override
    {
        NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_attributes);
        return _attributes->CopyAllItems(pDest);
    }
};
