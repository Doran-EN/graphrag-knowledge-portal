window.initGraphView = function (containerId, graphData, dotNetHelper) {
    var container = document.getElementById(containerId);
    if (!container) return;

    if (typeof d3 === 'undefined') {
        var script = document.createElement('script');
        script.src = 'https://cdnjs.cloudflare.com/ajax/libs/d3/7.8.5/d3.min.js';
        script.onload = function () {
            createHyperForceGraph(container, graphData, dotNetHelper);
        };
        document.head.appendChild(script);
    } else {
        createHyperForceGraph(container, graphData, dotNetHelper);
    }
};

window.focusGraphNode = function (nodeId) {
    if (window.activeGraphInstance) {
        window.activeGraphInstance.focusNode(nodeId);
    }
};

window.resetGraphSelection = function (resetCamera) {
    if (window.activeGraphInstance) {
        window.activeGraphInstance.resetSelection(resetCamera === true);
    }
};

window.resetGraphView = function () {
    if (window.activeGraphInstance) {
        window.activeGraphInstance.resetView();
    }
};

window.updateGraphPhysics = function (charge, linkDist, gravity) {
    if (window.activeGraphInstance) {
        window.activeGraphInstance.updatePhysics(charge, linkDist, gravity);
    }
};

window.filterGraphType = function (types) {
    if (window.activeGraphInstance) {
        window.activeGraphInstance.filterTypes(types);
    }
};

window.setGraphColorMode = function (mode) {
    if (window.activeGraphInstance) {
        window.activeGraphInstance.setColorMode(mode);
    }
};

window.setPinOnDrag = function (enabled) {
    if (window.activeGraphInstance) {
        window.activeGraphInstance.setPinOnDrag(enabled);
    }
};

window.unpinAllNodes = function () {
    if (window.activeGraphInstance) {
        window.activeGraphInstance.unpinAll();
    }
};

window.filterGraphGroup = function (groupName) {
    if (window.activeGraphInstance) {
        window.activeGraphInstance.filterGroup(groupName);
    }
};

function createHyperForceGraph(container, graphData, dotNetHelper) {
    container.innerHTML = '';

    var width = container.clientWidth || 900;
    var height = container.clientHeight || 650;

    var canvas = document.createElement('canvas');
    canvas.width = width * window.devicePixelRatio;
    canvas.height = height * window.devicePixelRatio;
    canvas.style.width = width + 'px';
    canvas.style.height = height + 'px';
    canvas.style.display = 'block';
    canvas.style.cursor = 'grab';
    container.appendChild(canvas);

    var ctx = canvas.getContext('2d');

    // Multi-group detection
    var hasMultipleGroups = (graphData.groups || []).length > 1;
    var groupColorMap = {};
    (graphData.groups || []).forEach(function (g) {
        if (g.name) {
            groupColorMap[g.name.toLowerCase()] = g.color;
        }
    });

    // Count actual connections for each node to scale node sizes
    var linkCountMap = {};
    (graphData.edges || []).forEach(function (e) {
        if (e.source && e.target) {
            linkCountMap[e.source] = (linkCountMap[e.source] || 0) + 1;
            linkCountMap[e.target] = (linkCountMap[e.target] || 0) + 1;
        }
    });

    // Node Radius Limits: min 3.2px, max 14.5px (strict cap to prevent infinite growth)
    var minRadius = 3.2;
    var maxRadius = 14.5;

    // Parse Nodes with connection-count scaling and hard upper cap
    var allNodes = (graphData.nodes || []).map(function (n) {
        var degree = linkCountMap[n.id] !== undefined ? linkCountMap[n.id] : (n.degree || 1);
        var isHub = n.label.toLowerCase().includes('index') ||
                    n.label.toLowerCase().includes('home') ||
                    degree >= 20;
        var grp = n.group || 'general';

        // Non-linear sqrt curve: grows with links, but strictly clamped at maxRadius
        var calculatedR = minRadius + Math.sqrt(degree) * 1.35;
        var baseRadius = Math.min(maxRadius, Math.max(minRadius, calculatedR));

        return {
            id: n.id,
            label: n.label || n.id,
            type: n.type || 'raw',
            summary: n.summary || '',
            degree: degree,
            group: grp,
            color: '#8e95b3',
            isHub: isHub,
            radius: baseRadius,
            pinned: false,
            visible: true
        };
    });

    var nodeMap = {};
    allNodes.forEach(function (n) { nodeMap[n.id] = n; });

    // Parse Links
    var allLinks = [];
    (graphData.edges || []).forEach(function (e) {
        if (nodeMap[e.source] && nodeMap[e.target]) {
            allLinks.push({
                source: e.source,
                target: e.target,
                label: e.label || ''
            });
        }
    });

    // If there are few edges (e.g. initial raw ingest), synthesize star links to Hub
    if (allLinks.length < 5 && allNodes.length > 5) {
        var hub = allNodes[0];
        hub.isHub = true;
        hub.radius = maxRadius;
        for (var i = 1; i < allNodes.length; i++) {
            if (i % 2 === 0) {
                allLinks.push({ source: hub.id, target: allNodes[i].id, label: 'raw_doc' });
            }
        }
    }

    var activeNodes = allNodes.slice();
    var activeLinks = allLinks.slice();
    var selectedGroupFilter = 'all';
    var activeTypeFilter = { entity: true, concept: true, source: true, raw: true };

    // Mode: 'gray' (default, Obsidian authentic monochromatic slate) or 'type' or 'group'
    var colorMode = 'gray';
    var pinOnDrag = false;

    function getNodeColor(node) {
        if (colorMode === 'gray') {
            // Obsidian default monochromatic gray: #8e95b3
            return '#8e95b3';
        } else if (colorMode === 'type') {
            if (node.isHub) return '#cba6f7';
            switch (node.type) {
                case 'entity': return '#a6e3a1'; // Emerald green
                case 'concept': return '#cba6f7'; // Mauve purple
                case 'source': return '#89b4fa';  // Blue
                case 'raw': return '#8e95b3';     // Slate gray
                default: return '#8e95b3';
            }
        } else {
            // By group/website
            if (hasMultipleGroups && groupColorMap[node.group.toLowerCase()]) {
                return groupColorMap[node.group.toLowerCase()];
            }
            return '#8e95b3';
        }
    }

    // Dynamic Physics Settings (Obsidian open-cluster layout with tight orphan constellation)
    var curCharge = -130;
    var curLinkDist = 58;
    var curGravity = 0.08;

    var simulation = d3.forceSimulation(activeNodes)
        .force('link', d3.forceLink(activeLinks).id(function (d) { return d.id; }).distance(curLinkDist).strength(0.6))
        .force('charge', d3.forceManyBody()
            .distanceMax(260)
            .strength(function (d) { 
                if (d.degree === 0) {
                    return -20; // gentle repulsion for orphan notes so they stay close to the cluster
                }
                if (d.isHub) {
                    return curCharge * 2.2;
                }
                return curCharge * (1 + Math.min(2.0, Math.sqrt(d.degree || 1) * 0.22)); 
            }))
        .force('center', d3.forceCenter(width / 2, height / 2).strength(curGravity))
        .force('x', d3.forceX(width / 2).strength(function (d) {
            return d.degree === 0 ? curGravity * 1.8 : curGravity * 0.6;
        }))
        .force('y', d3.forceY(height / 2).strength(function (d) {
            return d.degree === 0 ? curGravity * 1.8 : curGravity * 0.6;
        }))
        .force('collide', d3.forceCollide().radius(function (d) { return d.radius + 6; }).strength(0.75))
        .alphaDecay(0.02)
        .velocityDecay(0.38);

    var transform = d3.zoomIdentity;
    var hoveredNode = null;
    var selectedNode = null;
    var pulseRipples = [];

    // Drag state
    var dragSubject = null;
    var isDragging = false;
    var hasDragged = false;
    var dragStartX = 0;
    var dragStartY = 0;

    function getNodeAt(x, y) {
        var graphX = (x - transform.x) / transform.k;
        var graphY = (y - transform.y) / transform.k;

        for (var i = activeNodes.length - 1; i >= 0; i--) {
            var n = activeNodes[i];
            if (!n.visible) continue;
            var dx = graphX - n.x;
            var dy = graphY - n.y;
            var hitRadius = Math.max(12, n.radius + 6);
            if (dx * dx + dy * dy <= hitRadius * hitRadius) {
                return n;
            }
        }
        return null;
    }

    // D3 Zoom with filter to allow smooth node dragging
    var zoomBehavior = d3.zoom()
        .scaleExtent([0.08, 6.0])
        .filter(function (event) {
            // When mouse down is on a node, DO NOT start canvas pan/zoom
            // so canvas mousedown can initiate node drag!
            if (event.type === 'mousedown') {
                var rect = canvas.getBoundingClientRect();
                var node = getNodeAt(event.clientX - rect.left, event.clientY - rect.top);
                if (node) return false;
            }
            return (!event.ctrlKey || event.type === 'wheel') && !event.button;
        })
        .on('zoom', function (event) {
            transform = event.transform;
            render();
        });

    d3.select(canvas)
        .call(zoomBehavior)
        .on('dblclick.zoom', null);

    function render() {
        ctx.save();
        ctx.clearRect(0, 0, canvas.width, canvas.height);
        ctx.scale(window.devicePixelRatio, window.devicePixelRatio);

        // Obsidian Signature Canvas Background: #1e1e2e (pure seamless dark canvas)
        ctx.fillStyle = '#1e1e2e';
        ctx.fillRect(0, 0, width, height);

        ctx.save();
        ctx.translate(transform.x, transform.y);
        ctx.scale(transform.k, transform.k);

        // 1. Draw Expanding Pulse Ripples on Click
        for (var rIdx = pulseRipples.length - 1; rIdx >= 0; rIdx--) {
            var rip = pulseRipples[rIdx];
            rip.radius += 2.4;
            rip.alpha -= 0.025;
            if (rip.alpha <= 0) {
                pulseRipples.splice(rIdx, 1);
                continue;
            }
            ctx.beginPath();
            ctx.arc(rip.x, rip.y, rip.radius, 0, 2 * Math.PI);
            ctx.strokeStyle = rip.color;
            ctx.lineWidth = 2.0 / transform.k;
            ctx.globalAlpha = rip.alpha;
            ctx.stroke();
        }
        ctx.globalAlpha = 1.0;

        // 2. Draw Links (delicate, subtle lines matching Obsidian reference #565970)
        activeLinks.forEach(function (link) {
            var isConnectedToHover = hoveredNode && (link.source.id === hoveredNode.id || link.target.id === hoveredNode.id);
            var isConnectedToSelect = selectedNode && (link.source.id === selectedNode.id || link.target.id === selectedNode.id);
            var isConnectedToDrag = dragSubject && (link.source.id === dragSubject.id || link.target.id === dragSubject.id);

            ctx.beginPath();
            ctx.moveTo(link.source.x, link.source.y);
            ctx.lineTo(link.target.x, link.target.y);

            if (isConnectedToHover || isConnectedToSelect || isConnectedToDrag) {
                ctx.strokeStyle = isConnectedToDrag ? '#89b4fa' : '#cba6f7';
                ctx.lineWidth = Math.max(0.9, 1.1 / transform.k);
                ctx.globalAlpha = 0.9;
            } else if (hoveredNode || selectedNode || dragSubject) {
                var isZoomedIn = transform.k >= 0.92;
                ctx.strokeStyle = '#313244';
                ctx.lineWidth = 0.45 / transform.k;
                ctx.globalAlpha = isZoomedIn ? 0.10 : 0.02;
            } else {
                ctx.strokeStyle = '#565970';
                ctx.lineWidth = 0.55 / transform.k;
                ctx.globalAlpha = 0.25;
            }
            ctx.stroke();
        });
        ctx.globalAlpha = 1.0;

        var isZoomedIn = transform.k >= 0.92;

        // 3. Draw Regular Nodes (Pass 1 - excluding active node, which will be drawn on top in Pass 2)
        activeNodes.forEach(function (node) {
            if (!node.visible) return;

            var isHovered = hoveredNode && hoveredNode.id === node.id;
            var isSelected = selectedNode && selectedNode.id === node.id;
            var isDragged = dragSubject && dragSubject.id === node.id;
            if (isHovered || isSelected || isDragged) return; // Drawn on top in Pass 2

            var isNeighbor = (hoveredNode && activeLinks.some(function (l) {
                return (l.source.id === hoveredNode.id && l.target.id === node.id) ||
                       (l.target.id === hoveredNode.id && l.source.id === node.id);
            })) || (selectedNode && activeLinks.some(function (l) {
                return (l.source.id === selectedNode.id && l.target.id === node.id) ||
                       (l.target.id === selectedNode.id && l.source.id === node.id);
            })) || (dragSubject && activeLinks.some(function (l) {
                return (l.source.id === dragSubject.id && l.target.id === node.id) ||
                       (l.target.id === dragSubject.id && l.source.id === node.id);
            }));

            var color = getNodeColor(node);
            var r = node.radius;

            if (hoveredNode || selectedNode || dragSubject) {
                ctx.globalAlpha = isNeighbor ? 1.0 : (isZoomedIn ? 0.35 : 0.15);
            } else {
                ctx.globalAlpha = 1.0;
            }

            if (node.isHub) {
                ctx.shadowColor = color;
                ctx.shadowBlur = 8;
            } else {
                ctx.shadowBlur = 0;
            }

            ctx.beginPath();
            ctx.arc(node.x, node.y, r, 0, 2 * Math.PI);
            ctx.fillStyle = color;
            ctx.fill();

            // Subtle outline
            ctx.lineWidth = 1.2 / transform.k;
            ctx.strokeStyle = node.pinned ? '#ef4444' : 'rgba(24, 24, 37, 0.4)';
            ctx.stroke();
            ctx.shadowBlur = 0;

            // Labels for surrounding nodes:
            // - Zoomed in (isZoomedIn = true): show labels!
            //   If hovering: direct neighbors get crisp light text; other background nodes get subtle muted text (Obsidian style)
            // - Zoomed out (isZoomedIn = false): hide surrounding labels completely (only active node shown)
            var showSurroundingLabel = false;
            if (isZoomedIn) {
                showSurroundingLabel = true;
            }

            if (showSurroundingLabel) {
                var fontSize = Math.max(9, Math.min(13, 11 / Math.sqrt(transform.k)));
                ctx.font = (isNeighbor ? '600 ' : '400 ') + fontSize + 'px -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Inter, sans-serif';
                ctx.textAlign = 'center';
                ctx.textBaseline = 'top';

                var label = node.label;
                if (label.length > 28) label = label.substring(0, 26) + '...';

                ctx.lineJoin = 'round';
                ctx.miterLimit = 2;
                ctx.lineWidth = 2.6 / transform.k;
                ctx.strokeStyle = '#1e1e2e';
                ctx.strokeText(label, node.x, node.y + r + 3);

                if (hoveredNode || selectedNode || dragSubject) {
                    ctx.fillStyle = isNeighbor ? '#f1f5f9' : 'rgba(148, 163, 184, 0.45)';
                } else {
                    ctx.fillStyle = node.isHub ? '#e2e8f0' : 'rgba(215, 225, 238, 0.85)';
                }
                ctx.fillText(label, node.x, node.y + r + 3);
            }
        });

        // 4. Draw Active Node On Top (Pass 2)
        var activeNode = dragSubject || hoveredNode || selectedNode;
        if (activeNode && activeNode.visible) {
            ctx.globalAlpha = 1.0;
            var aColor = getNodeColor(activeNode);
            var aR = activeNode.radius + 3;

            // Glowing halo
            ctx.shadowColor = '#cba6f7';
            ctx.shadowBlur = 24;

            ctx.beginPath();
            ctx.arc(activeNode.x, activeNode.y, aR, 0, 2 * Math.PI);
            ctx.fillStyle = aColor;
            ctx.fill();

            // Crisp White Outline
            ctx.lineWidth = Math.max(1.8, 2.2 / transform.k);
            ctx.strokeStyle = selectedNode && selectedNode.id === activeNode.id ? '#f59e0b' : '#ffffff';
            ctx.stroke();
            ctx.shadowBlur = 0;

            // Large, prominent, crystal-clear label on zoom out; proportional and bold on zoom in
            var activeFontSize = isZoomedIn
                ? Math.max(12, 14 / Math.sqrt(transform.k))
                : Math.max(13, Math.min(50, 16.5 / transform.k));

            ctx.font = 'bold ' + activeFontSize + 'px Inter, -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif';
            ctx.textAlign = 'center';
            ctx.textBaseline = 'top';

            var activeLabel = activeNode.label;
            var textOffsetY = activeNode.y + aR + (isZoomedIn ? 3 : (4 / transform.k));

            // Dark outline halo in #1e1e2e so text is 100% readable over any lines
            ctx.lineJoin = 'round';
            ctx.miterLimit = 2;
            ctx.lineWidth = isZoomedIn ? (3.0 / transform.k) : Math.max(3.2, 4.2 / transform.k);
            ctx.strokeStyle = 'rgba(30, 30, 46, 0.95)';
            ctx.strokeText(activeLabel, activeNode.x, textOffsetY);

            // Pure bright white text fill
            ctx.fillStyle = '#ffffff';
            ctx.fillText(activeLabel, activeNode.x, textOffsetY);
        }

        ctx.restore();
        ctx.restore();
    }

    simulation.on('tick', render);

    // Node Dragging Implementation (Obsidian Elastic Spring Physics)
    canvas.addEventListener('mousedown', function (e) {
        var rect = canvas.getBoundingClientRect();
        var mouseX = e.clientX - rect.left;
        var mouseY = e.clientY - rect.top;
        var node = getNodeAt(mouseX, mouseY);

        if (node) {
            dragSubject = node;
            isDragging = true;
            hasDragged = false;
            dragStartX = e.clientX;
            dragStartY = e.clientY;
            canvas.style.cursor = 'grabbing';

            node.fx = (mouseX - transform.x) / transform.k;
            node.fy = (mouseY - transform.y) / transform.k;

            // Keep simulation hot so connected nodes elastically stretch like Obsidian
            simulation.alphaTarget(0.25).restart();
        }
    });

    window.addEventListener('mousemove', function (e) {
        var rect = canvas.getBoundingClientRect();
        var mouseX = e.clientX - rect.left;
        var mouseY = e.clientY - rect.top;

        if (isDragging && dragSubject) {
            if (Math.hypot(e.clientX - dragStartX, e.clientY - dragStartY) > 4) {
                hasDragged = true;
            }
            dragSubject.fx = (mouseX - transform.x) / transform.k;
            dragSubject.fy = (mouseY - transform.y) / transform.k;
            render();
            return;
        }

        if (mouseX >= 0 && mouseX <= width && mouseY >= 0 && mouseY <= height) {
            var node = getNodeAt(mouseX, mouseY);
            if (node !== hoveredNode) {
                hoveredNode = node;
                canvas.style.cursor = node ? 'pointer' : 'grab';
                render();
            }
        }
    });

    window.addEventListener('mouseup', function (e) {
        if (isDragging && dragSubject) {
            if (pinOnDrag) {
                dragSubject.pinned = true;
            } else if (!dragSubject.pinned) {
                // Elastic release: node settles smoothly into cluster
                dragSubject.fx = null;
                dragSubject.fy = null;
            }
            dragSubject = null;
            isDragging = false;
            canvas.style.cursor = 'grab';
            simulation.alphaTarget(0);
        }
    });

    // Double-click to Pin / Unpin Node
    canvas.addEventListener('dblclick', function (e) {
        var rect = canvas.getBoundingClientRect();
        var node = getNodeAt(e.clientX - rect.left, e.clientY - rect.top);
        if (node) {
            node.pinned = !node.pinned;
            if (node.pinned) {
                node.fx = node.x;
                node.fy = node.y;
            } else {
                node.fx = null;
                node.fy = null;
            }
            render();
        }
    });

    // Click to select node & create ripple pulse (ignores if drag occurred)
    canvas.addEventListener('click', function (e) {
        if (hasDragged) {
            hasDragged = false;
            return;
        }

        var rect = canvas.getBoundingClientRect();
        var node = getNodeAt(e.clientX - rect.left, e.clientY - rect.top);
        if (node) {
            selectedNode = node;
            pulseRipples.push({
                x: node.x,
                y: node.y,
                radius: node.radius,
                alpha: 1.0,
                color: '#cba6f7'
            });
            simulation.alpha(0.15).restart();

            if (dotNetHelper) {
                dotNetHelper.invokeMethodAsync('OnNodeSelected', node.id, node.label, node.type, node.summary);
            }
        } else {
            // Clicked on empty canvas background -> clear selection and notify Blazor
            if (selectedNode) {
                resetSelection(false);
                if (dotNetHelper) {
                    dotNetHelper.invokeMethodAsync('OnNodeDeselected');
                }
            }
        }
    });

    // Zoom & Focus onto a specific node
    function focusNode(nodeId) {
        var target = activeNodes.find(function (n) { return n.id.toLowerCase() === nodeId.toLowerCase(); });
        if (!target) return;

        selectedNode = target;
        pulseRipples.push({ x: target.x, y: target.y, radius: target.radius, alpha: 1.0, color: '#cba6f7' });

        var targetK = 1.6;
        var targetX = width / 2 - target.x * targetK;
        var targetY = height / 2 - target.y * targetK;

        d3.select(canvas).transition().duration(750).call(
            zoomBehavior.transform,
            d3.zoomIdentity.translate(targetX, targetY).scale(targetK)
        );
    }

    // Reset selection and optionally reset zoom camera
    function resetSelection(resetCamera) {
        selectedNode = null;
        render();
        if (resetCamera) {
            resetView();
        }
    }

    // Smoothly animate zoom and pan back to fit all visible nodes
    function resetView() {
        if (activeNodes.length === 0) {
            d3.select(canvas).transition().duration(600).call(
                zoomBehavior.transform,
                d3.zoomIdentity
            );
            return;
        }

        var minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
        var validCount = 0;
        activeNodes.forEach(function (n) {
            if (!n.visible || isNaN(n.x) || isNaN(n.y)) return;
            validCount++;
            if (n.x < minX) minX = n.x;
            if (n.x > maxX) maxX = n.x;
            if (n.y < minY) minY = n.y;
            if (n.y > maxY) maxY = n.y;
        });

        if (validCount === 0) {
            d3.select(canvas).transition().duration(600).call(
                zoomBehavior.transform,
                d3.zoomIdentity
            );
            return;
        }

        var pad = 90;
        var graphW = Math.max(100, (maxX - minX) + pad * 2);
        var graphH = Math.max(100, (maxY - minY) + pad * 2);

        var scale = Math.min(1.0, Math.max(0.15, Math.min(width / graphW, height / graphH)));
        var centerX = (minX + maxX) / 2;
        var centerY = (minY + maxY) / 2;
        var tx = width / 2 - centerX * scale;
        var ty = height / 2 - centerY * scale;

        d3.select(canvas).transition().duration(700).call(
            zoomBehavior.transform,
            d3.zoomIdentity.translate(tx, ty).scale(scale)
        );
    }

    // Press Escape to deselect (preserves current camera view)
    var onKeyDown = function (e) {
        if (e.key === 'Escape') {
            if (selectedNode) {
                resetSelection(false);
                if (dotNetHelper) {
                    dotNetHelper.invokeMethodAsync('OnNodeDeselected');
                }
            }
        }
    };
    window.addEventListener('keydown', onKeyDown);

    function updatePhysics(charge, linkDist, gravity) {
        curCharge = charge;
        curLinkDist = linkDist;
        curGravity = gravity;

        simulation
            .force('charge', d3.forceManyBody()
                .distanceMax(260)
                .strength(function (d) { 
                    if (d.degree === 0) return -20;
                    if (d.isHub) return curCharge * 2.2;
                    return curCharge * (1 + Math.min(2.0, Math.sqrt(d.degree || 1) * 0.22)); 
                }))
            .force('link', d3.forceLink(activeLinks).id(function (d) { return d.id; }).distance(curLinkDist).strength(0.6))
            .force('center', d3.forceCenter(width / 2, height / 2).strength(curGravity))
            .force('x', d3.forceX(width / 2).strength(function (d) {
                return d.degree === 0 ? curGravity * 1.8 : curGravity * 0.6;
            }))
            .force('y', d3.forceY(height / 2).strength(function (d) {
                return d.degree === 0 ? curGravity * 1.8 : curGravity * 0.6;
            }))
            .alpha(0.35).restart();
    }

    function applyCombinedFilter() {
        activeNodes = allNodes.filter(function (n) {
            var passType = activeTypeFilter[n.type] !== false;
            var passGroup = selectedGroupFilter === 'all' || (n.group || '').toLowerCase() === selectedGroupFilter.toLowerCase();
            return passType && passGroup;
        });

        var activeIds = {};
        activeNodes.forEach(function (n) { activeIds[n.id] = true; });

        activeLinks = allLinks.filter(function (l) {
            var src = l.source.id || l.source;
            var tgt = l.target.id || l.target;
            return activeIds[src] && activeIds[tgt];
        });

        simulation.nodes(activeNodes);
        simulation.force('link').links(activeLinks);
        simulation.alpha(0.35).restart();
    }

    function filterTypes(typeFilter) {
        activeTypeFilter = typeFilter;
        applyCombinedFilter();
    }

    function filterGroup(groupName) {
        selectedGroupFilter = groupName;
        applyCombinedFilter();
    }

    function setColorMode(mode) {
        colorMode = mode;
        render();
    }

    function setPinOnDragState(enabled) {
        pinOnDrag = enabled;
    }

    function unpinAll() {
        allNodes.forEach(function (n) {
            n.pinned = false;
            n.fx = null;
            n.fy = null;
        });
        simulation.alpha(0.35).restart();
    }

    // Resize observer
    if (window.ResizeObserver) {
        var ro = new ResizeObserver(function () {
            width = container.clientWidth || 900;
            height = container.clientHeight || 650;
            canvas.width = width * window.devicePixelRatio;
            canvas.height = height * window.devicePixelRatio;
            canvas.style.width = width + 'px';
            canvas.style.height = height + 'px';
            simulation.force('center', d3.forceCenter(width / 2, height / 2).strength(curGravity));
            simulation.force('x', d3.forceX(width / 2).strength(function (d) { return d.degree === 0 ? curGravity * 1.8 : curGravity * 0.6; }));
            simulation.force('y', d3.forceY(height / 2).strength(function (d) { return d.degree === 0 ? curGravity * 1.8 : curGravity * 0.6; }));
            render();
        });
        ro.observe(container);
    }

    window.activeGraphInstance = {
        focusNode: focusNode,
        resetSelection: resetSelection,
        resetView: resetView,
        updatePhysics: updatePhysics,
        filterTypes: filterTypes,
        filterGroup: filterGroup,
        setColorMode: setColorMode,
        setPinOnDrag: setPinOnDragState,
        unpinAll: unpinAll,
        simulation: simulation
    };
}
