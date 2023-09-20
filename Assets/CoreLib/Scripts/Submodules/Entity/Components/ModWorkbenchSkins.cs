﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using PugMod;
using SpriteInstancing;
using UnityEngine;

namespace CoreLib.Submodules.ModEntity.Components
{
    public class ModWorkbenchSkins : MonoBehaviour
    {
        private bool hasBeenApplied;
        [SerializeField] 
        internal SerializableDictionary<string, EntityMonoBehaviour.ReskinInfo> modReskinInfos = 
            new SerializableDictionary<string, EntityMonoBehaviour.ReskinInfo>();
        
        internal void Apply()
        {
            if (hasBeenApplied) return;

            CoreLibMod.Log.LogInfo("Applying mod workbench skins!");
            Debugger.Break();
            
            var craftingBuilding = gameObject.GetComponent<SimpleCraftingBuilding>();
            foreach (var pair in modReskinInfos)
            {
                pair.Value.objectIDToUseReskinOn = API.Authoring.GetObjectID(pair.Key);
                foreach (EntityMonoBehaviour.ReskinOptions reskinOption in craftingBuilding.reskinOptions)
                {
                    reskinOption.reskins.Add(pair.Value);
                }
            }
            hasBeenApplied = true;
        }
    }
}