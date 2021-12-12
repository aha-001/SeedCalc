// Copyright 2021 The Aha001 Team.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

using AgileMvvm;
using SeedLang.Common;

namespace SeedCalc {
  // The cutting board to show visualizable numbers and reference objects.
  public class CuttingBoard : MonoBehaviour {
    private class SlideAnimConfig {
      public GameObject Actor;
      public Vector3 FromPosition;
      public Vector3 ToPosition;
      public Vector3 FromScale;
      public Vector3 ToScale;
    }

    // The active and inactive colors for the CuttingBoard material.
    private static readonly Color _activeColor = new Color(.6f, .6f, .6f);
    private static readonly Color _inactiveColor = new Color(.14f, .27f, .26f);
    // The initial x offset of the rainbow texture.
    private const float _rainbowTexInitOffsetX = 0.05f;
    // The numbers of large rows and columns of the grid.
    private const int _LargeCellRows = 4;
    private const int _LargeCellCols = 6;
    // The trigger name to play the active animation when user clicks or touches on the object.
    private const string _activeAnimTriggerName = "Active";
    // The nubmer of animation steps (frames) when transitioning to the neighbor level.
    private const int _transitionAnimSteps = 20;
    // A number that is not visualizable so that it can be queued to turn off the indicator.
    private const int _nonVisualizableNumber = -1;

    public GameObject RootOfRefObjs;
    public GameObject RootOfDescPanels;
    public Texture RainbowTexture;
    public GameObject LightingMask;
    public Nav Nav;
    public Indicator Indicator;

    public AudioClip JumpToSound;
    public AudioClip SlideToSound;
    public AudioClip PlayAnimSound;

    private bool _active = false;
    private int _currentLevel = -1;
    // Queued numbers to be visualized.
    private readonly Queue<double> _numberQueue = new Queue<double>();
    // Map from (level, objName) to the description box of a reference object. A reference object
    // may appear in more than one levels and may have diffrent description boxes in different
    // levels.
    private Dictionary<(int level, string objName), GameObject> _descBoxes =
        new Dictionary<(int level, string objName), GameObject>();
    // Map from the reference object name to its container object and its own game object.
    private Dictionary<string, (GameObject Container, GameObject Obj)> _refObjs =
        new Dictionary<string, (GameObject Container, GameObject Obj)>();
    // The config indices of the left object and right object on each level. A level's left object
    // and right object can be randomly chosen from a list of candidates. This array is used to hold
    // the current objects showed on each level. leftObjIndex is set to -1 in case the level has
    // only one major object.
    private (int leftObjIndex, int rightObjIndex)[] _levelObjs =
        new (int leftObjIndex, int rightObjIndex)[LevelConfigs.Levels.Count];

    // Queues a new number to be visualized. A transition between two visualization levels might not
    // be completed within one frame, thus a queue is used to hold the numbers to be visualized. A
    // coroutine LevelTransitionLoop keeps running to peek numbers from the queue and play the
    // transition animation for them.
    public void QueueNewNumber(double number) {
      // No thread-safe protection is considered for now. All the accesses to the queue are from
      // either the main loop or a coroutine started by the main loop.
      _numberQueue.Enqueue(number);
    }

    public void OnCalculatorParsedExpressionUpdated(object sender, UpdatedEvent.Args args) {
      if (args.Value is null) {
        // In public interfaces, do NOT update the internal states (e.g., Indicator.Visible = false)
        // directly, since the queued numbers access the internal states in an asynchronous way.
        // It's recommended to queue a non-visualizable number to turn off the indicator.
        QueueNewNumber(_nonVisualizableNumber);
        return;
      }
      var parsedExpression = args.Value as ParsedExpression;
      if (!parsedExpression.BeingCalculated &&
          parsedExpression.SyntaxTokens.Count == 1 &&
          parsedExpression.SyntaxTokens[0].Type == SyntaxType.Number &&
          parsedExpression.TryParseNumber(0, out double number)) {
        QueueNewNumber(number);
      } else {
        QueueNewNumber(_nonVisualizableNumber);
      }
    }

    public void OnCalculatorResultUpdated(object sender, UpdatedEvent.Args args) {
      double? result = args.Value as double?;
      QueueNewNumber(result is null ? _nonVisualizableNumber : (double)result);
    }

    void Start() {
      SetActive(false);
      SetupRefObjs();
      SetupDescPanels();
      StartCoroutine(LevelTransitionLoop());
    }

    void Update() {
      if (Input.GetMouseButtonDown(0) && !EventSystem.current.IsPointerOverGameObject()) {
        var ray = Camera.main.ScreenPointToRay(Input.mousePosition);
        if (Physics.Raycast(ray, out RaycastHit hit, 100.0f)) {
          if (_refObjs.TryGetValue(hit.transform.name,
                                   out (GameObject Container, GameObject Obj) hitted)) {
            hitted.Obj.GetComponent<Animator>()?.SetTrigger(_activeAnimTriggerName);
            PlaySound(PlayAnimSound);
          }
        }
      }
    }

    // Turns the cutting board on/off.
    private void SetActive(bool active) {
      GetComponent<Renderer>().material.mainTexture = active ? RainbowTexture : null;
      GetComponent<Renderer>().material.color =  active ? _activeColor : _inactiveColor;
      LightingMask.SetActive(active);
      Nav.Show(active);
      Indicator.Visible = active;
      ShowRefObjsAtLevel(_currentLevel, active);
      if (active && _currentLevel < 0) {
        Indicator.Visible = false;
        Nav.SetNavLevel(Nav.DefaultLevel, Nav.DefaultMarkerValueString);
      }
      _active = active;
    }

    // The transition loop coroutine keeps running, peeking queued numbers and playing transition
    // animations.
    private IEnumerator LevelTransitionLoop() {
      while (true) {
        // So far the loop uses a simple strategy - only the last number is visualized if there are
        // more than one numbers have been queued during the last transition period.
        while (_numberQueue.Count > 1) {
          _numberQueue.Dequeue();
        }
        if (_numberQueue.Count == 1) {
          double number = _numberQueue.Dequeue();
          int level = LevelConfigs.MapNumberToLevel(number);
          if (level >= 0) {
            if (!_active) {
              SetActive(true);
            }
            // Hides indicator and desc panels during the transition.
            Indicator.Visible = false;
            ShowDescBoxesAtLevel(_currentLevel, false);
            if (_currentLevel >= 0 && (_currentLevel == level + 1 || _currentLevel == level - 1)) {
              // Slides to the left/right neighbor level.
              var animConfigs = PrepareSlideTransition(level, out GameObject objectToHideAfterAnim);
              for (int i = 1; i <= _transitionAnimSteps; i++) {
                // Adjusts positions and scales one step per frame.
                foreach (var animConfig in animConfigs) {
                  animConfig.Actor.transform.localPosition =
                      Vector3.Lerp(animConfig.FromPosition, animConfig.ToPosition,
                                   (float)i / (float)_transitionAnimSteps);
                  animConfig.Actor.transform.localScale =
                      Vector3.Lerp(animConfig.FromScale, animConfig.ToScale,
                                   (float)i / (float)_transitionAnimSteps);
                }
                yield return null;
              }
              if (!(objectToHideAfterAnim is null)) {
                objectToHideAfterAnim.SetActive(false);
              }
              PlaySound(SlideToSound);
            } else if (_currentLevel != level) {
              // Jumps to the target level directly. For now there is no transition animation when
              // jumping to a non-neighbor level.
              JumpToLevel(level);
              PlaySound(JumpToSound);
            }
            // Shows everything up once the transition is done.
            _currentLevel = level;
            Nav.SetNavLevel(LevelConfigs.Levels[level].NavLevel,
                            LevelConfigs.Levels[level].ScaleMarkerValueString);
            ScrollRainbowTo(LevelConfigs.Levels[level].NavLevel);
            Indicator.Visible = true;
            double indicatorMax = LevelConfigs.Levels[level].ScalePerLargeUnit * _LargeCellRows;
            Indicator.SetValue(indicatorMax, number);
            ShowDescBoxesAtLevel(_currentLevel, true);
          } else {
            // When a number is not able to be visualized, we do not turn the whole board to its
            // inactive mode. Instead, we simply hide the number indicator while keeping the
            // reference objects live on the board.
            Indicator.Visible = false;
          }
        }
        yield return null;
      }
    }

    private void SetupRefObjs() {
      _refObjs.Clear();
      for (int level = 0; level < LevelConfigs.Levels.Count; level++) {
        PreloadCandidateObjs(level);
        ChooseRefObjsRandomly();
      }
    }

    private void PreloadCandidateObjs(int level) {
      foreach (var refObjConfig in LevelConfigs.LeftAndRightCandidates(level)) {
        if (!_refObjs.ContainsKey(refObjConfig.ObjName)) {
          string containerName = LevelConfigs.GetContainerName(refObjConfig.ObjName);
          var container = RootOfRefObjs.transform.Find(containerName);
          Debug.Assert(!(container is null));
          var obj = container.transform.Find(refObjConfig.ObjName);
          obj.gameObject.SetActive(true);
          container.gameObject.SetActive(false);
          _refObjs.Add(refObjConfig.ObjName, (container.gameObject, obj.gameObject));
        }
      }
    }

    private void ChooseRefObjsRandomly() {
      for (int level = 0; level < LevelConfigs.Levels.Count; level++) {
        var config = LevelConfigs.Levels[level];
        int leftObjIndex = -1;
        if (!(config.LeftObjCandidates is null) && level > 0) {
          // The left/small object must be the same as the right/large object of the previous level.
          leftObjIndex = _levelObjs[level - 1].rightObjIndex;
        }
        Debug.Assert(!(config.RightObjCandidates is null));
        // The right/large object can be randomly chosen from the candidate list.
        int rightObjIndex = config.RightObjCandidates.Length <= 1 ? 0:
            Random.Range(0, config.RightObjCandidates.Length);
        _levelObjs[level] = (leftObjIndex, rightObjIndex);
      }
    }

    private void SetupDescPanels() {
      _descBoxes.Clear();
      for (int level = 0; level < LevelConfigs.Levels.Count; level++) {
        string descPanelName = LevelConfigs.GetDescPanelName(level);
        var panel = RootOfDescPanels.transform.Find(descPanelName);
        if (!(panel is null)) {
          panel.gameObject.SetActive(true);
          var config = LevelConfigs.Levels[level];
          foreach (var refObjConfig in LevelConfigs.LeftAndRightCandidates(level)) {
            string descName = LevelConfigs.GetDescName(refObjConfig.ObjName);
            var descBox = panel.Find(descName);
            if (!(descBox is null)) {
              descBox.gameObject.SetActive(false);
              _descBoxes.Add((level, refObjConfig.ObjName), descBox.gameObject);
            }
          }
        }
      }
    }

    private void JumpToLevel(int level) {
      ShowRefObjsAtLevel(_currentLevel, false);
      // Re-chooses reference objects randomly for all levels when jumping to a non-neighbor level.
      // On the other side, when the cuttong board slides to a neighbor level, the reference object
      // must not change since a object of the current level will be staying on the cutting board.
      ChooseRefObjsRandomly();
      ShowRefObjsAtLevel(level, true);
    }

    private void ShowRefObjsAtLevel(int level, bool show) {
      if (level >= 0) {
        var config = LevelConfigs.Levels[level];
        if (_levelObjs[level].leftObjIndex >= 0) {
          ShowRefObj(level, config.LeftObjCandidates[_levelObjs[level].leftObjIndex], true, show);
        }
        Debug.Assert(_levelObjs[level].rightObjIndex >= 0);
        ShowRefObj(level, config.RightObjCandidates[_levelObjs[level].rightObjIndex], false, show);
      }
    }

    private void ShowRefObj(int level, RefObjConfig refObjConfig, bool isLeftObj, bool show) {
      Debug.Assert(_refObjs.ContainsKey(refObjConfig.ObjName));
      var container = _refObjs[refObjConfig.ObjName].Container;
      if (show) {
        container.transform.localPosition = refObjConfig.InitialPosition;
        container.transform.localScale = LevelConfigs.CalcInitialScale(level, isLeftObj);
      }
      container.SetActive(show);
    }

    private void ShowDescBoxesAtLevel(int level, bool show) {
      if (level >= 0) {
        var config = LevelConfigs.Levels[level];
        if (_levelObjs[level].leftObjIndex >= 0) {
          string leftObjName = config.LeftObjCandidates[_levelObjs[level].leftObjIndex].ObjName;
          if (_descBoxes.TryGetValue((level, leftObjName), out var leftDescBox)) {
            leftDescBox.SetActive(show);
          }
        }
        Debug.Assert(_levelObjs[level].rightObjIndex >= 0);
        string rightObjName = config.RightObjCandidates[_levelObjs[level].rightObjIndex].ObjName;
        if (_descBoxes.TryGetValue((level, rightObjName), out var rightDescBox)) {
          rightDescBox.SetActive(show);
        }
      }
    }

    private IReadOnlyList<SlideAnimConfig> PrepareSlideTransition(
        int level, out GameObject objectToHideAfterAnim) {
      var animConfigs = new List<SlideAnimConfig>();
      var currentLevelConfig = LevelConfigs.Levels[_currentLevel];
      var targetLevelConfig = LevelConfigs.Levels[level];
      if (_currentLevel == level + 1) {
        // Slides to left.
        if (_levelObjs[level].leftObjIndex >= 0) {
          var leftConfig = targetLevelConfig.LeftObjCandidates[_levelObjs[level].leftObjIndex];
          var leftScale = LevelConfigs.CalcInitialScale(level, true);
          var leftContainer = _refObjs[leftConfig.ObjName].Container;
          leftContainer.transform.localPosition = leftConfig.VanishingPosition;
          leftContainer.transform.localScale = leftScale / 10.0f;
          leftContainer.SetActive(true);
          animConfigs.Add(new SlideAnimConfig {
            Actor = leftContainer,
            FromPosition = leftContainer.transform.localPosition,
            ToPosition = leftConfig.InitialPosition,
            FromScale = leftContainer.transform.localScale,
            ToScale = leftScale,
          });
        }

        var midConfig =
            currentLevelConfig.LeftObjCandidates[_levelObjs[_currentLevel].leftObjIndex];
        var midScale = LevelConfigs.CalcInitialScale(_currentLevel, true);
        var targetConfig = targetLevelConfig.RightObjCandidates[_levelObjs[level].rightObjIndex];
        var targetScale = LevelConfigs.CalcInitialScale(level, false);
        var midContainer = _refObjs[midConfig.ObjName].Container;
        animConfigs.Add(new SlideAnimConfig {
          Actor = midContainer,
          FromPosition = midConfig.InitialPosition,
          ToPosition = targetConfig.InitialPosition,
          FromScale = midScale,
          ToScale = targetScale,
        });

        var rightConfig =
            currentLevelConfig.RightObjCandidates[_levelObjs[_currentLevel].rightObjIndex];
        var rightScale = LevelConfigs.CalcInitialScale(_currentLevel, false);
        var rightContainer = _refObjs[rightConfig.ObjName].Container;
        animConfigs.Add(new SlideAnimConfig {
          Actor = rightContainer,
          FromPosition = rightConfig.InitialPosition,
          ToPosition = rightConfig.VanishingPosition,
          FromScale = rightScale,
          ToScale = rightScale * 10.0f,
        });
        objectToHideAfterAnim = rightContainer;
      } else if (_currentLevel == level - 1) {
        // Slides to right.
        if (_levelObjs[_currentLevel].leftObjIndex >= 0) {
          var leftConfig = currentLevelConfig.LeftObjCandidates[_levelObjs[_currentLevel].leftObjIndex];
          var leftScale = LevelConfigs.CalcInitialScale(_currentLevel, true);
          var leftContainer = _refObjs[leftConfig.ObjName].Container;
          animConfigs.Add(new SlideAnimConfig {
            Actor = leftContainer,
            FromPosition = leftConfig.InitialPosition,
            ToPosition = leftConfig.VanishingPosition,
            FromScale = leftScale,
            ToScale = leftScale / 10.0f,
          });
          objectToHideAfterAnim = leftContainer;
        } else {
          objectToHideAfterAnim = null;
        }

        var midConfig =
            currentLevelConfig.RightObjCandidates[_levelObjs[_currentLevel].rightObjIndex];
        var midScale = LevelConfigs.CalcInitialScale(_currentLevel, false);
        var targetConfig = targetLevelConfig.LeftObjCandidates[_levelObjs[level].leftObjIndex];
        var targetScale = LevelConfigs.CalcInitialScale(level, true);
        var midContainer = _refObjs[midConfig.ObjName].Container;
        animConfigs.Add(new SlideAnimConfig {
          Actor = midContainer,
          FromPosition = midConfig.InitialPosition,
          ToPosition = targetConfig.InitialPosition,
          FromScale = midScale,
          ToScale = targetScale,
        });

        var rightConfig = targetLevelConfig.RightObjCandidates[_levelObjs[level].rightObjIndex];
        var rightScale = LevelConfigs.CalcInitialScale(level, false);
        var rightContainer = _refObjs[rightConfig.ObjName].Container;
        rightContainer.transform.localPosition = rightConfig.VanishingPosition;
        rightContainer.transform.localScale = rightScale * 10.0f;
        rightContainer.SetActive(true);
        animConfigs.Add(new SlideAnimConfig {
          Actor = rightContainer,
          FromPosition = rightContainer.transform.localPosition,
          ToPosition = rightConfig.InitialPosition,
          FromScale = rightContainer.transform.localScale,
          ToScale = rightScale,
        });
      } else {
        throw new System.ArgumentException();
      }
      return animConfigs;
    }

    private void ScrollRainbowTo(int navLevel) {
      Debug.Assert(navLevel >= Nav.MinLevel && navLevel <= Nav.MaxLevel);
      int intervals = Nav.MaxLevel - Nav.MinLevel;
      float texOffsetX = (1.0f - _rainbowTexInitOffsetX) /
          intervals * (navLevel - Nav.MinLevel) + _rainbowTexInitOffsetX;
      if (texOffsetX > 1.0f) {
        texOffsetX -= 1.0f;
      }
      GetComponent<Renderer>().material.SetTextureOffset("_MainTex",
                                                         new UnityEngine.Vector2(texOffsetX, 0));
    }

    private void PlaySound(AudioClip audioClip, float volumeScale = 1f) {
      GetComponent<AudioSource>().PlayOneShot(audioClip, volumeScale);
    }
  }
}
