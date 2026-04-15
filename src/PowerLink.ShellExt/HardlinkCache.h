#pragma once

struct HardlinkCacheKey
{
    std::uint32_t volumeSerial;
    std::uint64_t fileId;

    bool operator==(const HardlinkCacheKey& other) const noexcept
    {
        return volumeSerial == other.volumeSerial && fileId == other.fileId;
    }
};

struct HardlinkCacheKeyHash
{
    std::size_t operator()(const HardlinkCacheKey& k) const noexcept
    {
        return std::hash<std::uint64_t>{}(k.fileId) ^
               (std::hash<std::uint32_t>{}(k.volumeSerial) << 1);
    }
};

class HardlinkCache
{
public:
    static constexpr std::size_t Capacity = 2048;
    static constexpr DWORD TtlMilliseconds = 10000;

    bool TryGet(const HardlinkCacheKey& key, bool& isHardlink)
    {
        std::lock_guard lock(_mutex);
        auto it = _map.find(key);
        if (it == _map.end()) return false;

        const DWORD now = GetTickCount();
        if (now - it->second.storedAtTick > TtlMilliseconds)
        {
            _lru.erase(it->second.lruIter);
            _map.erase(it);
            return false;
        }

        _lru.splice(_lru.begin(), _lru, it->second.lruIter);
        isHardlink = it->second.isHardlink;
        return true;
    }

    void Put(const HardlinkCacheKey& key, bool isHardlink)
    {
        std::lock_guard lock(_mutex);
        auto it = _map.find(key);
        if (it != _map.end())
        {
            it->second.isHardlink = isHardlink;
            it->second.storedAtTick = GetTickCount();
            _lru.splice(_lru.begin(), _lru, it->second.lruIter);
            return;
        }

        if (_lru.size() >= Capacity)
        {
            _map.erase(_lru.back());
            _lru.pop_back();
        }

        _lru.push_front(key);
        _map.emplace(key, Entry{ isHardlink, GetTickCount(), _lru.begin() });
    }

private:
    struct Entry
    {
        bool isHardlink;
        DWORD storedAtTick;
        std::list<HardlinkCacheKey>::iterator lruIter;
    };

    std::mutex _mutex;
    std::list<HardlinkCacheKey> _lru;
    std::unordered_map<HardlinkCacheKey, Entry, HardlinkCacheKeyHash> _map;
};
